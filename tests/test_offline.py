"""Checks that need no running game: metadata parsing and patch handling."""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from lcmem.monometa import Assembly, AssemblySet, MetadataError  # noqa: E402
from lcmem.patches import Patch, PatchError, PatchSet, encode, parse_aob  # noqa: E402

ASSEMBLY = Path(
    r"D:\SteamLibrary\steamapps\common\LoveCraft\Application"
    r"\Curio_Data\Managed\Assembly-CSharp.dll"
)

failures = 0


def check(label: str, ok: bool, detail: str = "") -> None:
    global failures
    print(f"[{'ok' if ok else 'FAIL'}] {label}" + (f": {detail}" if detail else ""))
    failures += not ok


def test_encoding() -> None:
    check("encode i32 hex", encode("0x40", "i32") == b"\x40\x00\x00\x00")
    check("encode u8 decimal", encode(64, "u8") == b"\x40")
    check("encode f32", encode("1.5", "f32") == b"\x00\x00\xc0\x3f")
    check("encode bytes", encode("90 90 90", "bytes") == b"\x90\x90\x90")
    try:
        encode(99999, "u8")
        check("encode rejects overflow", False)
    except Exception:
        check("encode rejects overflow", True)


def test_aob() -> None:
    data, mask = parse_aob("48 8B ?? 89")
    check("aob bytes", data == b"\x48\x8b\x00\x89", data.hex())
    check("aob mask", mask == b"\xff\xff\x00\xff", mask.hex())
    try:
        parse_aob("zz")
        check("aob rejects garbage", False)
    except PatchError:
        check("aob rejects garbage", True)


def test_patch_dict() -> None:
    p = Patch.from_dict(
        {"name": "x", "kind": "module", "module": "a.dll", "offset": "0x20",
         "type": "i32", "value": 5}
    )
    check("patch parses hex offset", p.offset == 0x20, hex(p.offset))
    p2 = Patch.from_dict({"name": "y", "kind": "mono", "class": "Foo", "field": "bar"})
    check("patch maps 'class' to klass", p2.klass == "Foo")
    try:
        Patch.from_dict({"name": "z", "typo": 1})
        check("patch rejects unknown keys", False)
    except PatchError:
        check("patch rejects unknown keys", True)
    try:
        Patch.from_dict({"kind": "module"})
        check("patch requires a name", False)
    except PatchError:
        check("patch requires a name", True)


def test_patchset() -> None:
    path = Path(__file__).resolve().parents[1] / "patches" / "community.json"
    ps = PatchSet.load(path)
    check("patchset loads", len(ps.patches) > 0, f"{len(ps.patches)} patches")
    check("templates ship disabled", all(not p.enabled for p in ps))
    try:
        ps.get("nope")
        check("patchset reports missing patch", False)
    except PatchError:
        check("patchset reports missing patch", True)


def test_metadata() -> None:
    if not ASSEMBLY.exists():
        print(f"[skip] metadata: {ASSEMBLY} not present")
        return
    a = Assembly(ASSEMBLY)
    check("assembly parses", len(a.types) > 1000, f"{len(a.types)} types")

    # This map was read out of the running game and confirmed field by field
    # (CameraFOV 65.0, CameraFOVSpeed 5.0, _defaultFov 65.0, folderName the
    # real screenshot path), so it pins Mono's actual layout rules:
    # MonoBehaviour subclasses start at 0x20, and reference fields are placed
    # before value fields regardless of declaration order.
    verified = {
        "_flashLight": 0x20, "_camera": 0x28, "_fadeBlendMaterial": 0x30,
        "_effects": 0x38, "folderName": 0x40, "screenshotPath": 0x48,
        "CameraFOVControl": 0x50, "CameraFOV": 0x54,
        "CameraFOVSpeed": 0x58, "_defaultFov": 0x5C,
    }
    cam = a.get("CameraFXManager")
    check("finds CameraFXManager", cam is not None)
    if cam:
        check("MonoBehaviour subclass starts at 0x20", cam.base_end == 0x20,
              hex(cam.base_end or 0))
        got = {f.name: f.predicted_offset for f in cam.instance_fields}
        wrong = {k: (hex(v), hex(got.get(k) or 0)) for k, v in verified.items()
                 if got.get(k) != v}
        check("CameraFXManager layout matches live memory", not wrong, str(wrong))
        fov = next((f for f in cam.fields if f.name == "CameraFOV"), None)
        check("CameraFOV typed as f32", fov is not None and fov.scan_type == "f32")
        refs = [f.predicted_offset for f in cam.instance_fields if f.is_reference]
        vals = [f.predicted_offset for f in cam.instance_fields if not f.is_reference]
        check("references precede value fields", max(refs) < min(vals),
              f"refs<={hex(max(refs))} vals>={hex(min(vals))}")

    structs = [t for t in a.types if t.extends == "System.ValueType"]
    check("value types start at offset 0", all(t.base_end == 0 for t in structs[:50]))

    # Static and const fields must never take up instance space.
    settings = a.get("Settings")
    if settings:
        offs = [f.predicted_offset for f in settings.fields if f.is_static]
        check("statics get no instance offset", all(o is None for o in offs))

    # Whatever the order, no two instance fields may overlap.
    def overlaps(t) -> bool:
        placed = sorted(
            (f.predicted_offset, f.size)
            for f in t.instance_fields
            if f.predicted_offset is not None and f.size
        )
        return any(a0 + s0 > b0 for (a0, s0), (b0, _) in zip(placed, placed[1:]))

    bad = [t.full_name for t in a.types if overlaps(t)]
    check("no type has overlapping fields", not bad, str(bad[:3]))

    enums = [t for t in a.types if t.extends == "System.Enum"]
    check("enum types found", len(enums) > 0, f"{len(enums)} enums")
    # A field typed as an enum should inherit the enum's underlying size.
    enum_named = {t.full_name for t in enums}
    sized = [
        f for t in a.types for f in t.fields
        if f.type_name in enum_named and f.scan_type is not None
    ]
    check("enum-typed fields get a scan type", len(sized) > 0, f"{len(sized)} fields")


def test_cross_assembly() -> None:
    """The chat types span three DLLs; loaded as a set they must resolve.

    These offsets are what lc_patches/chat_integration reads, so pin them.
    """
    managed = ASSEMBLY.parent
    if not managed.is_dir():
        print(f"[skip] cross-assembly: {managed} not present")
        return
    names = [
        "Assembly-CSharp.dll", "VWW.CoreLibs.ClientAPI.dll",
        "VWW.CoreLibs.Network.dll", "VWW.CoreLibs.Shared.dll",
        "mscorlib.dll",
    ]
    paths = [managed / n for n in names if (managed / n).exists()]
    aset = AssemblySet(paths)
    check("assembly set loads", len(aset.assemblies) >= 4, repr(aset))

    expected = {
        "ChatChannelInfo": {
            "<ChannelName>k__BackingField": 0x10,
            "m_DisplayName": 0x18,
            "<ChannelMemberOfWhomITryToCommunicateWith>k__BackingField": 0x28,
            "m_Messages": 0x50,
            "<IsLocal>k__BackingField": 0x69,
            "<IsPrivate>k__BackingField": 0x6A,
            "<IsSystem>k__BackingField": 0x6B,
        },
        # <Participant> is declared on the base type, so it is checked there.
        "ChatChannelUserEventArgs": {"<Participant>k__BackingField": 0x28},
        "ChatChannelMessageEventArgs": {
            "<Message>k__BackingField": 0x30,
            "<TimeStamp>k__BackingField": 0x40,
            "<Style>k__BackingField": 0x48,
            "<ColorLevel>k__BackingField": 0x4D,
        },
        "ChannelMember": {"<Name>k__BackingField": 0x10},
        # Confirms the reference-first rule on a type we did not write:
        # _syncRoot is a reference so it lands before _size.
        "List`1": {"_items": 0x10, "_syncRoot": 0x18, "_size": 0x20},
    }
    for type_name, fields in expected.items():
        t = aset.get(type_name)
        if t is None:
            check(f"{type_name} resolves", False, "not found")
            continue
        got = {f.name: f.predicted_offset for f in t.instance_fields}
        wrong = {
            k: (hex(v), hex(got[k]) if got.get(k) is not None else None)
            for k, v in fields.items()
            if got.get(k) != v
        }
        check(f"{type_name} layout", not wrong, str(wrong))

    # Where cross-assembly loading actually earns its keep: <ColorLevel> is
    # typed as an enum defined in VWW.CoreLibs.Network. Without that DLL its
    # size is unknown, so prediction stops there and the field gets no offset.
    def color_level(source) -> int | None:
        t = source.get("ChatChannelMessageEventArgs")
        if t is None:
            return None
        f = next(
            (f for f in t.instance_fields if f.name == "<ColorLevel>k__BackingField"),
            None,
        )
        return f.predicted_offset if f else None

    solo = Assembly(managed / "VWW.CoreLibs.ClientAPI.dll")
    check(
        "enum size needs the assembly that defines it",
        color_level(solo) is None and color_level(aset) == 0x4D,
        f"alone={color_level(solo)} together={color_level(aset)}",
    )
    # And types from another DLL are only reachable at all as a set.
    check(
        "sibling assembly types are reachable",
        solo.get("ChatChannelInfo") is None and aset.get("ChatChannelInfo") is not None,
    )


def test_bad_file() -> None:
    tmp = Path(__file__).parent / "_notpe.bin"
    tmp.write_bytes(b"not a pe file at all")
    try:
        Assembly(tmp)
        check("rejects non-PE input", False)
    except MetadataError:
        check("rejects non-PE input", True)
    finally:
        tmp.unlink()


if __name__ == "__main__":
    test_encoding()
    test_aob()
    test_patch_dict()
    test_patchset()
    test_metadata()
    test_cross_assembly()
    test_bad_file()
    print()
    print("FAILURES:", failures)
    sys.exit(1 if failures else 0)

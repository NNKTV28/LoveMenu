"""Read class and field layout straight out of Assembly-CSharp.dll.

The game ships Mono (not IL2CPP), so its C# metadata is intact on disk.
Parsing it turns a blind byte hunt into "AccountManager has an int field
called Balance, third instance field, so try +0x18" - which is a far better
starting point for a scan than a raw number.

This reads the ECMA-335 tables directly; no external tooling required.

One caveat worth stating plainly: .NET metadata does not record instance
field *offsets* for ordinary (auto-layout) classes - the runtime assigns
them when the type is loaded. `predicted_offset` below reproduces Mono's
own algorithm: resolve the inheritance chain, then place fields with
natural alignment, reference-typed fields first (see `_layout_order`).

That has been checked against this game's live memory - every field of
CameraFXManager lands where predicted - but it is still a reconstruction.
Verify with `lcmem object <addr>` before writing anything.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field as dc_field
from pathlib import Path

# --- ECMA-335 element types ------------------------------------------------
ET_VOID = 0x01
ET_BOOLEAN = 0x02
ET_CHAR = 0x03
ET_I1 = 0x04
ET_U1 = 0x05
ET_I2 = 0x06
ET_U2 = 0x07
ET_I4 = 0x08
ET_U4 = 0x09
ET_I8 = 0x0A
ET_U8 = 0x0B
ET_R4 = 0x0C
ET_R8 = 0x0D
ET_STRING = 0x0E
ET_PTR = 0x0F
ET_BYREF = 0x10
ET_VALUETYPE = 0x11
ET_CLASS = 0x12
ET_VAR = 0x13
ET_ARRAY = 0x14
ET_GENERICINST = 0x15
ET_TYPEDBYREF = 0x16
ET_I = 0x18
ET_U = 0x19
ET_FNPTR = 0x1B
ET_OBJECT = 0x1C
ET_SZARRAY = 0x1D
ET_MVAR = 0x1E
ET_CMOD_REQD = 0x1F
ET_CMOD_OPT = 0x20

#: element type -> (scanner type name, size in bytes)
PRIMITIVES: dict[int, tuple[str, int]] = {
    ET_BOOLEAN: ("u8", 1),
    ET_CHAR: ("u16", 2),
    ET_I1: ("i8", 1),
    ET_U1: ("u8", 1),
    ET_I2: ("i16", 2),
    ET_U2: ("u16", 2),
    ET_I4: ("i32", 4),
    ET_U4: ("u32", 4),
    ET_I8: ("i64", 8),
    ET_U8: ("u64", 8),
    ET_R4: ("f32", 4),
    ET_R8: ("f64", 8),
}

REFERENCE_TYPES = {ET_STRING, ET_CLASS, ET_OBJECT, ET_SZARRAY, ET_ARRAY, ET_PTR, ET_FNPTR, ET_I, ET_U}

FIELD_STATIC = 0x0010
FIELD_LITERAL = 0x0040

#: vtable pointer + sync block that precede every managed object on x64
OBJECT_HEADER = 0x10

#: Where a subclass's own fields begin, for base types outside this assembly.
#: UnityEngine.Object carries a single IntPtr (m_CachedPtr) pointing at the
#: native object; Component and Behaviour add nothing on top of that.
EXTERNAL_BASE_END: dict[str, int] = {
    "System.Object": OBJECT_HEADER,
    "UnityEngine.Object": OBJECT_HEADER + 8,
    "UnityEngine.Component": OBJECT_HEADER + 8,
    "UnityEngine.Behaviour": OBJECT_HEADER + 8,
    # MonoBehaviour adds its own IntPtr (the destroy cancellation token
    # source in current Unity), so subclass fields start 8 bytes later.
    "UnityEngine.MonoBehaviour": OBJECT_HEADER + 16,
    "UnityEngine.ScriptableObject": OBJECT_HEADER + 8,
    "System.ValueType": 0,
    "System.Enum": 0,
    # EventArgs and Exception-free bases carry no instance fields of their own.
    "System.EventArgs": OBJECT_HEADER,
    "System.Attribute": OBJECT_HEADER,
}

#: Sizes for value types whose definition we cannot see (they live in
#: assemblies we do not parse, or are runtime intrinsics).
KNOWN_STRUCT_SIZES: dict[str, int] = {
    "UnityEngine.Vector2": 8, "UnityEngine.Vector3": 12,
    "UnityEngine.Vector4": 16, "UnityEngine.Quaternion": 16,
    "UnityEngine.Color": 16, "UnityEngine.Color32": 4,
    "UnityEngine.Rect": 16, "UnityEngine.Bounds": 24,
    "System.Guid": 16, "System.DateTime": 8, "System.TimeSpan": 8,
    "System.DateTimeOffset": 16, "System.Decimal": 16,
    "System.IntPtr": 8, "System.UIntPtr": 8,
    "System.Boolean": 1, "System.Char": 2,
    "System.Byte": 1, "System.SByte": 1,
    "System.Int16": 2, "System.UInt16": 2,
    "System.Int32": 4, "System.UInt32": 4,
    "System.Int64": 8, "System.UInt64": 8,
    "System.Single": 4, "System.Double": 8,
}

# Table ids we care about.
T_MODULE, T_TYPEREF, T_TYPEDEF, T_FIELDPTR, T_FIELD, T_METHODDEF = 0, 1, 2, 3, 4, 6


class MetadataError(RuntimeError):
    pass


@dataclass
class FieldInfo:
    name: str
    flags: int
    element_type: int
    type_name: str
    size: int              # 0 when the size is not statically known
    scan_type: str | None  # a Scanner type name, when it maps to one
    #: a GC-tracked reference (Mono lays these out before everything else).
    #: IntPtr and unmanaged pointers are deliberately not references.
    is_reference: bool = False
    predicted_offset: int | None = None

    @property
    def is_static(self) -> bool:
        return bool(self.flags & FIELD_STATIC)

    @property
    def is_const(self) -> bool:
        return bool(self.flags & FIELD_LITERAL)

    @property
    def is_instance(self) -> bool:
        return not (self.is_static or self.is_const)


@dataclass
class TypeInfo:
    name: str
    namespace: str
    flags: int
    extends: str
    fields: list[FieldInfo] = dc_field(default_factory=list)
    #: offset at which this type's own fields start, once inheritance is
    #: resolved; None when some base in the chain has an unknown layout
    base_end: int | None = None

    @property
    def full_name(self) -> str:
        return f"{self.namespace}.{self.name}" if self.namespace else self.name

    @property
    def instance_fields(self) -> list[FieldInfo]:
        return [f for f in self.fields if f.is_instance]

    def find(self, needle: str) -> list[FieldInfo]:
        n = needle.lower()
        return [f for f in self.fields if n in f.name.lower()]

    def __repr__(self) -> str:
        return f"<Type {self.full_name} fields={len(self.fields)}>"


class _Reader:
    """Little-endian cursor over a bytes buffer."""

    def __init__(self, data: bytes, pos: int = 0):
        self.data = data
        self.pos = pos

    def u8(self) -> int:
        v = self.data[self.pos]
        self.pos += 1
        return v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self.data, self.pos)[0]
        self.pos += 2
        return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return v

    def u64(self) -> int:
        v = struct.unpack_from("<Q", self.data, self.pos)[0]
        self.pos += 8
        return v

    def index(self, size: int) -> int:
        return self.u16() if size == 2 else self.u32()


def _compressed(data: bytes, pos: int) -> tuple[int, int]:
    """Decode an ECMA-335 compressed unsigned integer. Returns (value, new_pos)."""
    b = data[pos]
    if b & 0x80 == 0:
        return b, pos + 1
    if b & 0xC0 == 0x80:
        return ((b & 0x3F) << 8) | data[pos + 1], pos + 2
    return (
        ((b & 0x1F) << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3],
        pos + 4,
    )


class Assembly:
    """Parsed type/field metadata for one managed assembly."""

    def __init__(
        self,
        path: str | Path,
        registry: dict[str, "TypeInfo"] | None = None,
        finalize: bool = True,
    ):
        """Parse one assembly.

        `registry` is a name -> type map shared with sibling assemblies, so
        a class can inherit from, or embed a struct defined in, another DLL.
        Pass finalize=False when loading a set; call `finalize()` once every
        assembly has registered its types (see `AssemblySet`).
        """
        self.path = Path(path)
        self.data = self.path.read_bytes()
        self.types: list[TypeInfo] = []
        self._by_name: dict[str, TypeInfo] = {}
        self.registry = self._by_name if registry is None else registry
        self._struct_size_cache: dict[str, int | None] = {}
        self._parse()
        if finalize:
            self.finalize()

    # --- PE / CLI plumbing -------------------------------------------------
    def _rva_to_offset(self, rva: int) -> int:
        for va, vsize, raw_ptr, raw_size in self._sections:
            if va <= rva < va + max(vsize, raw_size):
                return raw_ptr + (rva - va)
        raise MetadataError(f"RVA 0x{rva:X} is outside every section")

    def _parse_pe(self) -> int:
        d = self.data
        if d[:2] != b"MZ":
            raise MetadataError(f"{self.path.name} is not a PE image")
        pe = struct.unpack_from("<I", d, 0x3C)[0]
        if d[pe : pe + 4] != b"PE\0\0":
            raise MetadataError("bad PE signature")
        coff = pe + 4
        n_sections = struct.unpack_from("<H", d, coff + 2)[0]
        opt_size = struct.unpack_from("<H", d, coff + 16)[0]
        opt = coff + 20
        magic = struct.unpack_from("<H", d, opt)[0]
        if magic == 0x10B:
            dirs = opt + 96
        elif magic == 0x20B:
            dirs = opt + 112
        else:
            raise MetadataError(f"unknown optional header magic 0x{magic:X}")

        sec = opt + opt_size
        self._sections = []
        for i in range(n_sections):
            off = sec + i * 40
            vsize, va, raw_size, raw_ptr = struct.unpack_from("<IIII", d, off + 8)
            self._sections.append((va, vsize, raw_ptr, raw_size))

        # Data directory 14 is the CLI header.
        cli_rva = struct.unpack_from("<I", d, dirs + 14 * 8)[0]
        if not cli_rva:
            raise MetadataError("not a managed assembly (no CLI header)")
        cli = self._rva_to_offset(cli_rva)
        meta_rva = struct.unpack_from("<I", d, cli + 8)[0]
        return self._rva_to_offset(meta_rva)

    def _parse_streams(self, meta: int) -> None:
        d = self.data
        if d[meta : meta + 4] != b"BSJB":
            raise MetadataError("bad metadata signature")
        ver_len = struct.unpack_from("<I", d, meta + 12)[0]
        p = meta + 16 + ver_len
        p += 2  # flags
        n_streams = struct.unpack_from("<H", d, p)[0]
        p += 2
        self._streams: dict[str, tuple[int, int]] = {}
        for _ in range(n_streams):
            offset, size = struct.unpack_from("<II", d, p)
            p += 8
            end = d.index(b"\0", p)
            name = d[p:end].decode("ascii")
            p = end + 1
            p = (p + 3) & ~3  # names are padded to a 4-byte boundary
            self._streams[name] = (meta + offset, size)

    def _string(self, idx: int) -> str:
        base, size = self._streams["#Strings"]
        if idx >= size:
            return ""
        end = self.data.index(b"\0", base + idx)
        return self.data[base + idx : end].decode("utf-8", errors="replace")

    def _blob(self, idx: int) -> bytes:
        base, _ = self._streams["#Blob"]
        length, pos = _compressed(self.data, base + idx)
        return self.data[pos : pos + length]

    # --- table parsing -----------------------------------------------------
    def _parse(self) -> None:
        meta = self._parse_pe()
        self._parse_streams(meta)
        if "#~" not in self._streams:
            raise MetadataError("no #~ table stream (unsupported metadata form)")
        base, _ = self._streams["#~"]
        d = self.data

        heap_sizes = d[base + 6]
        self._str_size = 4 if heap_sizes & 0x01 else 2
        self._guid_size = 4 if heap_sizes & 0x02 else 2
        self._blob_size = 4 if heap_sizes & 0x04 else 2

        valid = struct.unpack_from("<Q", d, base + 8)[0]
        present = [i for i in range(64) if valid >> i & 1]
        p = base + 24
        rows: dict[int, int] = {}
        for t in present:
            rows[t] = struct.unpack_from("<I", d, p)[0]
            p += 4
        self.rows = rows

        def simple(table: int) -> int:
            return 4 if rows.get(table, 0) >= (1 << 16) else 2

        def coded(tables: tuple[int, ...], tag_bits: int) -> int:
            biggest = max((rows.get(t, 0) for t in tables), default=0)
            return 4 if biggest >= (1 << (16 - tag_bits)) else 2

        type_def_or_ref = coded((T_TYPEDEF, T_TYPEREF, 0x1B), 2)
        resolution_scope = coded((T_MODULE, 0x1A, 0x23, T_TYPEREF), 2)
        field_idx = simple(T_FIELD)
        method_idx = simple(T_METHODDEF)

        # Walk tables in id order until we have what we need; each table's
        # rows are fixed-width, so sizes let us skip the ones we ignore.
        row_size = {
            T_MODULE: 2 + self._str_size + 3 * self._guid_size,
            T_TYPEREF: resolution_scope + 2 * self._str_size,
            T_TYPEDEF: 4 + 2 * self._str_size + type_def_or_ref + field_idx + method_idx,
            T_FIELDPTR: field_idx,
            T_FIELD: 2 + self._str_size + self._blob_size,
        }

        offsets: dict[int, int] = {}
        cursor = p
        for t in present:
            offsets[t] = cursor
            if t not in row_size:
                raise MetadataError(
                    f"table 0x{t:02X} appears before the ones we need; unsupported layout"
                )
            cursor += row_size[t] * rows[t]
            if t >= T_FIELD:
                break

        typerefs = self._read_typerefs(offsets, rows, resolution_scope)
        fields = self._read_fields(offsets, rows)
        self._read_typedefs(offsets, rows, type_def_or_ref, field_idx, method_idx,
                            typerefs, fields)


    def finalize(self) -> None:
        """Resolve value-type sizes and assign field offsets.

        Split out from parsing so a set of assemblies can all register their
        types first, letting cross-assembly bases and structs resolve.
        """
        self._resolve_enums()
        self._assign_offsets()

    def _read_typerefs(self, offsets, rows, resolution_scope) -> list[str]:
        out: list[str] = [""]  # metadata indices are 1-based
        if T_TYPEREF not in offsets:
            return out
        r = _Reader(self.data, offsets[T_TYPEREF])
        for _ in range(rows[T_TYPEREF]):
            r.index(resolution_scope)
            name = self._string(r.index(self._str_size))
            ns = self._string(r.index(self._str_size))
            out.append(f"{ns}.{name}" if ns else name)
        return out

    def _read_fields(self, offsets, rows) -> list[tuple[str, int, bytes]]:
        out: list[tuple[str, int, bytes]] = [("", 0, b"")]
        if T_FIELD not in offsets:
            return out
        r = _Reader(self.data, offsets[T_FIELD])
        for _ in range(rows[T_FIELD]):
            flags = r.u16()
            name = self._string(r.index(self._str_size))
            sig = self._blob(r.index(self._blob_size))
            out.append((name, flags, sig))
        return out

    def _read_typedefs(
        self, offsets, rows, type_def_or_ref, field_idx, method_idx, typerefs, fields
    ) -> None:
        if T_TYPEDEF not in offsets:
            return
        r = _Reader(self.data, offsets[T_TYPEDEF])
        raw: list[tuple[str, str, int, int, int]] = []
        for _ in range(rows[T_TYPEDEF]):
            flags = r.u32()
            name = self._string(r.index(self._str_size))
            ns = self._string(r.index(self._str_size))
            extends = r.index(type_def_or_ref)
            field_list = r.index(field_idx)
            r.index(method_idx)
            raw.append((name, ns, flags, extends, field_list))

        n_fields = rows.get(T_FIELD, 0)
        for i, (name, ns, flags, extends, field_list) in enumerate(raw):
            end = raw[i + 1][4] if i + 1 < len(raw) else n_fields + 1
            tag, idx = extends & 3, extends >> 2
            if tag == 0 and 0 < idx <= len(raw):
                base_name = raw[idx - 1][1] + "." + raw[idx - 1][0] if raw[idx - 1][1] else raw[idx - 1][0]
            elif tag == 1 and 0 < idx < len(typerefs):
                base_name = typerefs[idx]
            else:
                base_name = ""
            t = TypeInfo(name=name, namespace=ns, flags=flags, extends=base_name)
            for fi in range(field_list, min(end, len(fields))):
                fname, fflags, fsig = fields[fi]
                et, tname, size, scan, ref = self._parse_field_sig(fsig, typerefs, raw)
                t.fields.append(FieldInfo(fname, fflags, et, tname, size, scan, ref))
            self.types.append(t)
            self._by_name[t.full_name] = t
            self.registry.setdefault(t.full_name, t)

    def _parse_field_sig(self, sig: bytes, typerefs, raw_types):
        """Classify a field signature.

        Returns (element type, type name, size, scan type, is_reference).
        """
        if not sig:
            return 0, "?", 0, None, False
        pos = 0
        if sig[pos] == 0x06:  # FIELD calling convention
            pos += 1
        # Skip custom modifiers.
        while pos < len(sig) and sig[pos] in (ET_CMOD_REQD, ET_CMOD_OPT):
            pos += 1
            _, pos = _compressed(sig, pos)
        if pos >= len(sig):
            return 0, "?", 0, None, False
        et = sig[pos]
        if et in PRIMITIVES:
            scan, size = PRIMITIVES[et]
            return et, scan, size, scan, False
        if et == ET_STRING:
            return et, "string", 8, None, True
        if et == ET_OBJECT:
            return et, "object", 8, None, True
        if et in (ET_CLASS, ET_VALUETYPE):
            tname = self._token_name(sig, pos + 1, typerefs, raw_types)
            if et == ET_CLASS:
                return et, tname, 8, None, True
            return et, tname, 0, None, False  # value type: size resolved later
        if et in (ET_SZARRAY, ET_ARRAY):
            return et, "array", 8, None, True
        if et == ET_GENERICINST:
            # GENERICINST is followed by CLASS or VALUETYPE, which decides
            # whether the instance is a reference (List<T>) or a struct
            # (Nullable<T>) - and so which layout pass it belongs to.
            inner = sig[pos + 1] if pos + 1 < len(sig) else ET_CLASS
            tname = self._token_name(sig, pos + 2, typerefs, raw_types)
            if inner == ET_VALUETYPE:
                return et, f"{tname}<>", 0, None, False
            return et, f"{tname}<>", 8, None, True
        if et in (ET_VAR, ET_MVAR):
            return et, "T", 8, None, True
        # IntPtr, UIntPtr and raw pointers are values, not GC references.
        if et in (ET_I, ET_U, ET_PTR, ET_FNPTR):
            return et, "IntPtr", 8, None, False
        return et, f"et_0x{et:02X}", 0, None, False

    def _token_name(self, sig: bytes, pos: int, typerefs, raw_types) -> str:
        """Resolve a TypeDefOrRef coded token in a signature to a name."""
        if pos >= len(sig):
            return "?"
        token, _ = _compressed(sig, pos)
        tag, idx = token & 3, token >> 2
        if tag == 0 and 0 < idx <= len(raw_types):
            nm = raw_types[idx - 1]
            return f"{nm[1]}.{nm[0]}" if nm[1] else nm[0]
        if tag == 1 and 0 < idx < len(typerefs):
            return typerefs[idx]
        return "?"

    def _enum_underlying(self, name: str) -> tuple[str, int] | None:
        """(scan type, size) for an enum, looked up across all assemblies."""
        t = self.registry.get(name)
        if t is None or t.extends != "System.Enum":
            return None
        for f in t.fields:
            if f.name == "value__" and f.scan_type:
                return f.scan_type, f.size
        return None

    def _struct_size(self, name: str, seen: frozenset[str] = frozenset()) -> int | None:
        """Size of a value type, resolved recursively across assemblies.

        Returns None when any member is itself unresolvable, so an unknown
        size never silently becomes a wrong offset.
        """
        if name in KNOWN_STRUCT_SIZES:
            return KNOWN_STRUCT_SIZES[name]
        if name in self._struct_size_cache:
            return self._struct_size_cache[name]
        if name in seen:
            return None  # cyclic struct definition; refuse to guess
        t = self.registry.get(name)
        if t is None or t.extends not in ("System.ValueType", "System.Enum"):
            return None
        self._struct_size_cache[name] = None  # guard against recursion
        end = 0
        biggest = 1
        for f in t.instance_fields:
            size = f.size
            if not size and f.element_type == ET_VALUETYPE:
                size = self._struct_size(f.type_name, seen | {name}) or 0
            if not size:
                return None
            align = min(size, 8)
            biggest = max(biggest, align)
            end = ((end + align - 1) & ~(align - 1)) + size
        end = (end + biggest - 1) & ~(biggest - 1)  # trailing padding
        self._struct_size_cache[name] = end
        return end

    def _resolve_enums(self) -> None:
        """Give value-type fields a concrete size where one can be derived."""
        for t in self.types:
            for f in t.fields:
                if f.element_type != ET_VALUETYPE or f.size:
                    continue
                enum = self._enum_underlying(f.type_name)
                if enum:
                    f.scan_type, f.size = enum
                    continue
                size = self._struct_size(f.type_name)
                if size:
                    f.size = size

    def _base_end(self, t: TypeInfo, seen: frozenset[str] = frozenset()) -> int | None:
        """Offset at which this type's own fields begin.

        Walks the inheritance chain so a MonoBehaviour subclass starts after
        UnityEngine.Object's m_CachedPtr rather than at the bare header.
        Returns None when some base in the chain has an unknown layout.
        """
        if t.full_name in seen:
            return None  # cyclic metadata; refuse to guess
        # Value types and enums have no object header.
        if t.extends in ("System.ValueType", "System.Enum"):
            return 0
        if t.extends in EXTERNAL_BASE_END:
            return EXTERNAL_BASE_END[t.extends]
        base = self.registry.get(t.extends)
        if base is None:
            # Unknown external base: only System.Object is safe to assume.
            return OBJECT_HEADER if t.extends in ("", "System.Object") else None
        inner = self._base_end(base, seen | {t.full_name})
        if inner is None:
            return None
        for f in base.instance_fields:
            if not f.size:
                return None
            align = min(f.size, 8)
            inner = ((inner + align - 1) & ~(align - 1)) + f.size
        return inner

    def _layout_order(self, t: TypeInfo) -> list[FieldInfo]:
        """The order Mono actually assigns offsets in.

        For a reference class that holds any GC references, Mono uses a
        "GC aware" layout: it runs two passes over the fields, placing the
        reference-typed ones first and everything else after, so the GC can
        scan a compact run of pointers. Within each pass declaration order
        is preserved. Value types and reference-free classes get a single
        pass, which is plain declaration order.

        This is not a detail we can skip: for a class that interleaves
        object references with numbers, single-pass ordering puts every
        field after the first reference at the wrong offset.
        """
        fields = t.instance_fields
        is_valuetype = t.extends in ("System.ValueType", "System.Enum")
        if is_valuetype or not any(f.is_reference for f in fields):
            return fields
        return [f for f in fields if f.is_reference] + [
            f for f in fields if not f.is_reference
        ]

    def _assign_offsets(self) -> None:
        """Predict each instance field's offset.

        Prediction stops at the first field whose size is unknown, since
        everything placed after it would be guesswork.
        """
        for t in self.types:
            off = self._base_end(t)
            t.base_end = off
            if off is None:
                continue
            for f in self._layout_order(t):
                if not f.size:
                    f.predicted_offset = None
                    break
                align = min(f.size, 8)
                off = (off + align - 1) & ~(align - 1)
                f.predicted_offset = off
                off += f.size

    # --- lookup ------------------------------------------------------------
    def get(self, name: str) -> TypeInfo | None:
        if name in self._by_name:
            return self._by_name[name]
        for t in self.types:
            if t.name == name:
                return t
        return None

    def search(self, needle: str) -> list[TypeInfo]:
        n = needle.lower()
        return [t for t in self.types if n in t.full_name.lower()]

    def search_fields(self, needle: str) -> list[tuple[TypeInfo, FieldInfo]]:
        n = needle.lower()
        return [(t, f) for t in self.types for f in t.fields if n in f.name.lower()]

    def __repr__(self) -> str:
        return f"<Assembly {self.path.name} types={len(self.types)}>"


class AssemblySet:
    """Several assemblies parsed together, so types resolve across them.

    A class in Assembly-CSharp routinely inherits from, or embeds a struct
    defined in, VWW.CoreLibs.*. Parsed alone, those fields have unknown size
    and offset prediction stops at them. Loaded as a set, they resolve.
    """

    def __init__(self, paths: list[str | Path]):
        self.registry: dict[str, TypeInfo] = {}
        self.assemblies: list[Assembly] = []
        for path in paths:
            try:
                self.assemblies.append(Assembly(path, self.registry, finalize=False))
            except MetadataError:
                continue  # a native or non-managed DLL in the folder
        for asm in self.assemblies:
            asm.finalize()

    @classmethod
    def from_folder(cls, folder: str | Path, pattern: str = "*.dll") -> "AssemblySet":
        return cls(sorted(Path(folder).glob(pattern)))

    @property
    def types(self) -> list[TypeInfo]:
        return [t for a in self.assemblies for t in a.types]

    def get(self, name: str) -> TypeInfo | None:
        if name in self.registry:
            return self.registry[name]
        for t in self.types:
            if t.name == name:
                return t
        return None

    def search(self, needle: str) -> list[TypeInfo]:
        n = needle.lower()
        return [t for t in self.types if n in t.full_name.lower()]

    def search_fields(self, needle: str) -> list[tuple[TypeInfo, FieldInfo]]:
        n = needle.lower()
        return [(t, f) for t in self.types for f in t.fields if n in f.name.lower()]

    def __repr__(self) -> str:
        return f"<AssemblySet {len(self.assemblies)} assemblies, {len(self.registry)} types>"

"""Declarative patches: describe a change once, apply and revert it safely.

A patch is JSON, so a fix can be shared as a file rather than as a list of
addresses that stop being true the moment the game restarts. Each patch says
how to *find* its target rather than hardcoding an address:

* ``module``  - an offset inside a loaded module (stable across launches)
* ``aob``     - a byte signature with ``??`` wildcards, the usual way code
                patches survive game updates
* ``mono``    - a class name plus a field name, resolved through metadata

Every apply records the bytes it overwrote, so `revert` restores exactly
what was there. Backups live alongside the patch file and are keyed by the
process start time, so a stale backup from a previous launch is never
replayed into a fresh process.
"""
from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterator

import numpy as np

from .mono import Mono
from . import win32 as w
from .process import Process, ProcessError

ENCODERS: dict[str, tuple[str, type]] = {
    "i8": ("<b", int), "u8": ("<B", int),
    "i16": ("<h", int), "u16": ("<H", int),
    "i32": ("<i", int), "u32": ("<I", int),
    "i64": ("<q", int), "u64": ("<Q", int),
    "f32": ("<f", float), "f64": ("<d", float),
}


#: any PAGE_EXECUTE_* protection - where code patches live
EXECUTABLE = (
    w.PAGE_EXECUTE | w.PAGE_EXECUTE_READ
    | w.PAGE_EXECUTE_READWRITE | w.PAGE_EXECUTE_WRITECOPY
)


class PatchError(RuntimeError):
    pass


def encode(value: Any, type_name: str) -> bytes:
    """Turn a patch's value into the bytes to write."""
    if type_name == "bytes":
        text = value.replace(" ", "").replace("-", "")
        return bytes.fromhex(text)
    if type_name not in ENCODERS:
        raise PatchError(f"unknown type {type_name!r}; pick from {sorted(ENCODERS)} or 'bytes'")
    fmt, caster = ENCODERS[type_name]
    if isinstance(value, str):
        # Accept "0x40", "0b1010" and plain decimal from the command line.
        value = int(value, 0) if caster is int else float(value)
    return struct.pack(fmt, caster(value))


def parse_aob(pattern: str) -> tuple[bytes, bytes]:
    """Parse "48 8B ?? 89" into (bytes, mask) where mask is 0xFF or 0x00."""
    tokens = pattern.replace(",", " ").split()
    if not tokens:
        raise PatchError("empty AOB pattern")
    data = bytearray()
    mask = bytearray()
    for tok in tokens:
        if tok in ("??", "?", "*"):
            data.append(0)
            mask.append(0)
        else:
            try:
                data.append(int(tok, 16))
            except ValueError as exc:
                raise PatchError(f"bad byte {tok!r} in AOB pattern") from exc
            mask.append(0xFF)
    return bytes(data), bytes(mask)


def find_aob(
    proc: Process, pattern: str, executable_only: bool = True, limit: int = 64, progress=None
) -> list[int]:
    """Search memory for a byte signature, honouring ``??`` wildcards.

    Code patches normally live in executable image pages, which is the
    default; pass executable_only=False to sweep data as well.
    """
    data, mask = parse_aob(pattern)
    n = len(data)
    arr_pat = np.frombuffer(data, dtype=np.uint8)
    arr_mask = np.frombuffer(mask, dtype=np.uint8)
    care = np.flatnonzero(arr_mask)
    if not len(care):
        raise PatchError("AOB pattern is entirely wildcards")
    first = int(care[0])

    out: list[int] = []
    regions = [
        r
        for r in proc.regions()
        if not executable_only or r.protect & EXECUTABLE
    ]
    for i, region in enumerate(regions):
        if progress:
            progress(i, len(regions))
        for base, blob in proc.read_chunks(region):
            if len(blob) < n:
                continue
            buf = np.frombuffer(blob, dtype=np.uint8)
            # Anchor on the first non-wildcard byte, then verify candidates.
            cand = np.flatnonzero(buf[: len(buf) - n + 1 + first] == arr_pat[first])
            cand = cand - first
            cand = cand[(cand >= 0) & (cand <= len(buf) - n)]
            for c in cand:
                window = buf[c : c + n]
                if np.all((window & arr_mask) == (arr_pat & arr_mask)):
                    out.append(base + int(c))
                    if len(out) >= limit:
                        if progress:
                            progress(len(regions), len(regions))
                        return out
    if progress:
        progress(len(regions), len(regions))
    return out


@dataclass
class Patch:
    name: str
    description: str = ""
    kind: str = "module"          # module | aob | mono
    type: str = "i32"
    value: Any = 0
    enabled: bool = True

    # module locator
    module: str = ""
    offset: int = 0

    # aob locator
    pattern: str = ""
    pattern_offset: int = 0
    executable_only: bool = True

    # mono locator
    klass: str = ""
    namespace: str | None = None
    field: str = ""
    instance: int = 0

    @classmethod
    def from_dict(cls, d: dict) -> "Patch":
        known = {f for f in cls.__dataclass_fields__}
        unknown = set(d) - known - {"class"}
        if unknown:
            raise PatchError(f"patch {d.get('name','?')!r} has unknown keys: {sorted(unknown)}")
        data = {k: v for k, v in d.items() if k in known}
        if "class" in d:  # 'class' is friendlier in JSON than 'klass'
            data["klass"] = d["class"]
        for key in ("offset", "pattern_offset"):
            if isinstance(data.get(key), str):
                data[key] = int(data[key], 0)
        if "name" not in data:
            raise PatchError("every patch needs a name")
        return cls(**data)

    def resolve(self, proc: Process, mono: Mono | None = None) -> int:
        """Work out the address this patch targets, in the running process."""
        if self.kind == "module":
            if not self.module:
                raise PatchError(f"{self.name}: module patch needs a 'module'")
            return proc.module(self.module).base + self.offset
        if self.kind == "aob":
            hits = find_aob(proc, self.pattern, self.executable_only, limit=8)
            if not hits:
                raise PatchError(f"{self.name}: AOB pattern not found")
            if len(hits) > 1:
                raise PatchError(
                    f"{self.name}: AOB pattern matched {len(hits)} places; make it more specific"
                )
            return hits[0] + self.pattern_offset
        if self.kind == "mono":
            return self._resolve_mono(proc, mono)
        raise PatchError(f"{self.name}: unknown patch kind {self.kind!r}")

    def _resolve_mono(self, proc: Process, mono: Mono | None) -> int:
        if mono is None or mono.assembly is None:
            raise PatchError(f"{self.name}: mono patches need metadata and a bootstrapped Mono")
        info = mono.assembly.get(self.klass)
        if info is None:
            raise PatchError(f"{self.name}: no type named {self.klass!r} in the assembly")
        matches = [f for f in info.instance_fields if f.name == self.field]
        if not matches:
            raise PatchError(f"{self.name}: {self.klass} has no instance field {self.field!r}")
        fld = matches[0]
        if fld.predicted_offset is None:
            raise PatchError(f"{self.name}: offset of {self.klass}.{self.field} is unknown")
        classes = mono.find_class(info.name, self.namespace)
        if not classes:
            raise PatchError(f"{self.name}: class {self.klass} not loaded in the process")
        instances = mono.find_instances(classes[0])
        if not len(instances):
            raise PatchError(f"{self.name}: no live instances of {self.klass}")
        if self.instance >= len(instances):
            raise PatchError(
                f"{self.name}: asked for instance {self.instance} but only "
                f"{len(instances)} exist"
            )
        return int(instances[self.instance]) + fld.predicted_offset

    def payload(self) -> bytes:
        return encode(self.value, self.type)


@dataclass
class PatchSet:
    path: Path
    patches: list[Patch] = field(default_factory=list)

    @classmethod
    def load(cls, path: str | Path) -> "PatchSet":
        p = Path(path)
        raw = json.loads(p.read_text(encoding="utf-8"))
        items = raw["patches"] if isinstance(raw, dict) else raw
        return cls(p, [Patch.from_dict(d) for d in items])

    def __iter__(self) -> Iterator[Patch]:
        return iter(self.patches)

    def get(self, name: str) -> Patch:
        for p in self.patches:
            if p.name == name:
                return p
        raise PatchError(f"no patch named {name!r} in {self.path.name}")


class Backups:
    """Original bytes for applied patches, scoped to one process lifetime."""

    def __init__(self, path: str | Path, proc: Process):
        self.path = Path(path)
        self.pid = proc.pid
        self.data: dict[str, dict] = {}
        if self.path.exists():
            try:
                stored = json.loads(self.path.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                stored = {}
            # Addresses are only meaningful within the process that produced
            # them; drop anything recorded against a different PID.
            if stored.get("pid") == self.pid:
                self.data = stored.get("entries", {})

    def save(self) -> None:
        self.path.write_text(
            json.dumps({"pid": self.pid, "entries": self.data}, indent=2), encoding="utf-8"
        )

    def record(self, name: str, addr: int, original: bytes) -> None:
        self.data[name] = {"address": addr, "original": original.hex()}
        self.save()

    def take(self, name: str) -> tuple[int, bytes] | None:
        entry = self.data.pop(name, None)
        if entry is None:
            return None
        self.save()
        return entry["address"], bytes.fromhex(entry["original"])

    def __contains__(self, name: str) -> bool:
        return name in self.data


def apply_patch(
    proc: Process, patch: Patch, backups: Backups, mono: Mono | None = None
) -> tuple[int, bytes, bytes]:
    """Apply one patch. Returns (address, original bytes, written bytes)."""
    addr = patch.resolve(proc, mono)
    payload = patch.payload()
    original = proc.read(addr, len(payload))
    if original == payload:
        return addr, original, payload  # already in the desired state
    proc.write(addr, payload)
    readback = proc.read(addr, len(payload))
    if readback != payload:
        proc.write(addr, original)
        raise PatchError(
            f"{patch.name}: wrote to 0x{addr:X} but it did not stick "
            "(the game may be overwriting it every frame)"
        )
    backups.record(patch.name, addr, original)
    return addr, original, payload


def revert_patch(proc: Process, name: str, backups: Backups) -> tuple[int, bytes] | None:
    """Restore the bytes a patch overwrote. Returns (address, bytes) or None."""
    entry = backups.take(name)
    if entry is None:
        return None
    addr, original = entry
    try:
        proc.write(addr, original)
    except ProcessError as exc:
        raise PatchError(f"could not restore {name} at 0x{addr:X}: {exc}") from exc
    return addr, original

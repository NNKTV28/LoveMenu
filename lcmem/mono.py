"""Runtime Mono introspection, from outside the process.

Every managed object begins with a pointer to its MonoVTable, and every
vtable begins with a pointer to its MonoClass, which holds the type's name.
Following that chain answers the question a raw scan cannot: *what is this
address actually part of?*

MonoClass's field offsets differ between Mono builds, so rather than
hardcoding them this module discovers them at runtime: given any real
object, it looks for two adjacent pointers inside MonoClass that both land
on plausible ASCII identifiers (`name` and `name_space` are neighbours in
every Mono version). When the on-disk metadata is available the discovered
name is checked against it, which turns a guess into a confirmation.

No code is injected and no threads are created in the target; this is all
reads of the game's own data structures.
"""
from __future__ import annotations

import re
from dataclasses import dataclass

import numpy as np

from .monometa import Assembly, TypeInfo
from .process import (
    MONO_ARRAY_COUNT,
    MONO_ARRAY_DATA,
    MONO_OBJ_HEADER,
    MONO_STRING_CHARS,
    Process,
    ProcessError,
)

# System.Collections.Generic.List<T>, confirmed against the game's corlib:
# _items and _syncRoot are references so Mono places them first, which
# pushes _size past where a naive declaration-order guess would put it.
LIST_ITEMS = 0x10
LIST_SIZE = 0x20

#: how far into MonoClass to look for the name/name_space pair
CLASS_SEARCH_BYTES = 0x200

#: bytes that can appear inside an identifier, used to reject partial matches
_IDENT_BYTES = frozenset(
    b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_"
)

#: a plausible C# type name (including generics and compiler-generated names)
IDENT = re.compile(r"^[A-Za-z_<][A-Za-z0-9_`<>.@\-+$]{0,127}$")


class MonoError(RuntimeError):
    pass


@dataclass
class ClassLayout:
    """Discovered offsets inside MonoClass."""

    name: int
    name_space: int

    def __repr__(self) -> str:
        return f"<ClassLayout name=+0x{self.name:X} ns=+0x{self.name_space:X}>"


class Mono:
    def __init__(self, proc: Process, assembly: Assembly | None = None):
        self.proc = proc
        self.assembly = assembly
        self.layout: ClassLayout | None = None
        self._known: set[str] = set()
        if assembly:
            self._known = {t.name for t in assembly.types}
        self._name_cache: dict[int, tuple[str, str]] = {}
        self._vtable_cache: set[int] = set()
        self._not_vtable: set[int] = set()
        self._heap_regions: list | None = None
        self._class_cache: dict[str, int] = {}
        #: an address inside Mono's metadata string heap, learned during
        #: discovery; every type name lives near it
        self._name_heap_hint: int | None = None
        #: an address inside Mono's vtable/class pool, learned during
        #: discovery; every other vtable lives near it
        self._vtable_hint: int | None = None
        self._candidate_regions: list | None = None

    # --- the object -> class chain ----------------------------------------
    def vtable_of(self, obj: int) -> int:
        return self.proc.ptr(obj)

    def class_of(self, obj: int) -> int:
        """MonoClass address for an object pointer."""
        vt = self.proc.ptr(obj)
        if not self._plausible_pointer(vt):
            raise MonoError(f"0x{obj:X} does not look like a managed object")
        return self.proc.ptr(vt)

    @staticmethod
    def _plausible_pointer(p: int) -> bool:
        """A pointer to a structure - those are always 8-aligned on x64."""
        return 0x10000 < p < 0x7FFFFFFFFFFF and p % 8 == 0

    @staticmethod
    def _in_user_range(p: int) -> bool:
        """A pointer to *anything*. Names live in the metadata heap packed
        end to end, so they carry no alignment guarantee at all."""
        return 0x10000 < p < 0x7FFFFFFFFFFF

    def _ascii_at(self, addr: int, limit: int = 128) -> str | None:
        if not self._in_user_range(addr):
            return None
        raw = self.proc.try_read(addr, limit)
        if not raw or raw[0] in (0, 0x20):
            return None
        end = raw.find(b"\0")
        if end <= 0:
            return None
        try:
            text = raw[:end].decode("ascii")
        except UnicodeDecodeError:
            return None
        return text if IDENT.match(text) else None

    # --- layout discovery --------------------------------------------------
    def find_strings(
        self, text: str, limit: int = 64, progress=None
    ) -> list[int]:
        """Locate managed System.String objects whose content starts with `text`.

        Returns *object* addresses (not character addresses). Each hit is
        validated against the MonoString header - a plausible vtable pointer
        and a length consistent with the text found - which filters out the
        many raw UTF-16 buffers that are not managed strings.
        """
        pattern = text.encode("utf-16-le")
        narrowed = self.candidate_regions()
        out = self._scan_strings(pattern, len(text), limit, narrowed, progress)
        if out:
            return out
        # Nothing in the likely regions; fall back to everything private.
        everywhere = [
            r for r in self.proc.regions(writable_only=True) if r.type_name == "private"
        ]
        return self._scan_strings(pattern, len(text), limit, everywhere, progress)

    def _scan_strings(
        self, pattern: bytes, text_len: int, limit: int, regions, progress=None
    ) -> list[int]:
        out: list[int] = []
        for i, region in enumerate(regions):
            if progress:
                progress(i, len(regions))
            for base, data in self.proc.read_chunks(region):
                pos = data.find(pattern)
                while pos != -1:
                    obj = base + pos - MONO_STRING_CHARS
                    if self._is_string_object(obj, text_len):
                        out.append(obj)
                        if len(out) >= limit:
                            if progress:
                                progress(len(regions), len(regions))
                            return out
                    pos = data.find(pattern, pos + 1)
        if progress:
            progress(len(regions), len(regions))
        return out

    def _is_string_object(self, obj: int, min_len: int) -> bool:
        if obj < 0x10000 or obj % 4:
            return False
        head = self.proc.try_read(obj, MONO_STRING_CHARS)
        if not head or len(head) < MONO_STRING_CHARS:
            return False
        vtable = int.from_bytes(head[:8], "little")
        if not self._plausible_pointer(vtable):
            return False
        length = int.from_bytes(head[0x10:0x14], "little")
        if not min_len <= length <= 1 << 20:
            return False
        # The vtable must itself point at something pointer-shaped (the class).
        klass = self.proc.try_read(vtable, 8)
        return bool(klass) and self._plausible_pointer(int.from_bytes(klass, "little"))

    def bootstrap(self, hint: str = "Lovecraft", progress=None) -> ClassLayout:
        """Discover the MonoClass layout with no prior knowledge.

        Finds real System.String objects by their known layout and uses them
        as samples, confirming against the name "String" - which is what
        their class must be called if the offset is correct.
        """
        samples = self.find_strings(hint, limit=24, progress=progress)
        if not samples:
            raise MonoError(
                f"no managed strings containing {hint!r} found; "
                "try a different --hint that is visible in game"
            )
        return self.discover(samples, expect={"String"})

    def discover(
        self, sample_objects: list[int], expect: set[str] | None = None, min_confirm: int = 1
    ) -> ClassLayout:
        """Work out where `name`/`name_space` sit inside MonoClass.

        `sample_objects` are candidate managed object pointers - any real
        objects will do. An offset is accepted when it yields a valid
        identifier pair for a sample, and the name is one we expected
        (`expect`, defaulting to the types the assembly defines), which
        turns a plausible guess into a confirmed one.
        """
        known = expect if expect is not None else self._known
        scores: dict[int, int] = {}
        confirmed: dict[int, int] = {}
        for obj in sample_objects:
            try:
                klass = self.class_of(obj)
            except (MonoError, ProcessError):
                continue
            if not self._plausible_pointer(klass):
                continue
            blob = self.proc.try_read(klass, CLASS_SEARCH_BYTES)
            if not blob or len(blob) < CLASS_SEARCH_BYTES:
                continue
            ptrs = np.frombuffer(blob, dtype=np.uint64)
            for i in range(len(ptrs) - 1):
                name = self._ascii_at(int(ptrs[i]))
                if not name:
                    continue
                # name_space may legitimately be the empty string.
                ns_ptr = int(ptrs[i + 1])
                ns_raw = self.proc.try_read(ns_ptr, 2) if self._in_user_range(ns_ptr) else None
                if ns_raw is None:
                    continue
                ns_ok = ns_raw[:1] == b"\0" or self._ascii_at(ns_ptr) is not None
                if not ns_ok:
                    continue
                off = i * 8
                scores[off] = scores.get(off, 0) + 1
                if name in known:
                    confirmed[off] = confirmed.get(off, 0) + 1

        pool = confirmed or scores
        if not pool:
            raise MonoError(
                "could not locate MonoClass.name - none of the sample addresses "
                "looked like managed objects"
            )
        best = max(pool, key=lambda o: (pool[o], -o))
        if confirmed and confirmed[best] < min_confirm:
            raise MonoError("MonoClass layout found but not confirmed against metadata")
        self.layout = ClassLayout(name=best, name_space=best + 8)
        # Remember where that name string lived; every other type name in
        # the process sits in the same metadata heap, which lets later
        # lookups search a window instead of the whole address space.
        for obj in sample_objects:
            try:
                vtable = self.proc.ptr(obj)
                ptr = self.proc.ptr(self.class_of(obj) + self.layout.name)
            except (MonoError, ProcessError):
                continue
            if self._in_user_range(ptr):
                self._name_heap_hint = ptr
                self._vtable_hint = vtable
                break
        return self.layout

    def ensure_layout(self, sample_objects: list[int]) -> ClassLayout:
        if self.layout is None:
            self.discover(sample_objects)
        assert self.layout is not None
        return self.layout

    # --- naming ------------------------------------------------------------
    def class_name(self, klass: int) -> tuple[str, str]:
        """(namespace, name) for a MonoClass address."""
        if self.layout is None:
            raise MonoError("call discover() first")
        if klass in self._name_cache:
            return self._name_cache[klass]
        name = self._ascii_at(self.proc.ptr(klass + self.layout.name)) or ""
        ns_ptr = self.proc.ptr(klass + self.layout.name_space)
        ns = self._ascii_at(ns_ptr) or ""
        self._name_cache[klass] = (ns, name)
        return ns, name

    def identify(self, obj: int) -> str:
        """Full type name of the object at `obj`, or '' if it isn't one."""
        try:
            klass = self.class_of(obj)
            ns, name = self.class_name(klass)
        except (MonoError, ProcessError):
            return ""
        if not name:
            return ""
        return f"{ns}.{name}" if ns else name

    def identify_containing(self, addr: int, back: int = 0x400) -> tuple[int, str] | None:
        """Find the object that `addr` sits inside.

        Walks backwards over 8-byte-aligned candidates looking for a header
        that resolves to a named class. Returns (object address, type name).
        """
        start = addr & ~7
        for delta in range(0, back + 1, 8):
            cand = start - delta
            if cand < 0x10000:
                break
            name = self.identify(cand)
            if name:
                return cand, name
        return None

    # --- finding a class by name ------------------------------------------
    def find_class(
        self, name: str, namespace: str | None = None, near: int = 512 << 20, progress=None
    ) -> list[int]:
        """MonoClass addresses for a type name, without injecting anything.

        Locates the name in Mono's metadata heap, then finds the structures
        that point at it - those are the MonoClass records, since we know
        where `name` sits inside one.

        Mono keeps its metadata heap and class pool in the same arena, so
        the pointer sweep is restricted to a window around the string
        (`near` bytes either side) and only widens if that finds nothing.
        """
        if self.layout is None:
            raise MonoError("call bootstrap() or discover() first")
        pattern = name.encode("ascii") + b"\0"

        # The same text also appears in the mapped copy of the assembly file,
        # so a windowed search can come back with matches that are real text
        # but not the metadata heap. Only classes actually found settle it -
        # widen and retry whenever the narrow pass yields none.
        attempts = []
        hint = self._name_heap_hint
        if hint:
            attempts.append((max(0, hint - near), hint + near))
        attempts.append((0, 1 << 63))

        for lo_s, hi_s in attempts:
            needles = self._scan_ascii(pattern, progress, lo=lo_s, hi=hi_s)
            if not needles:
                continue
            found = self._classes_from_names(needles, name, namespace, near, progress)
            if found:
                return found
        return []

    def _classes_from_names(
        self, needles: list[int], name: str, namespace: str | None, near: int, progress
    ) -> list[int]:
        """Turn metadata-name addresses into the MonoClasses pointing at them."""
        assert self.layout is not None
        lo = max(0, min(needles) - near)
        hi = max(needles) + near
        hits = self._scan_pointers_to(set(needles), progress, lo=lo, hi=hi)
        if not len(hits):
            hits = self._scan_pointers_to(set(needles), progress)

        out: list[int] = []
        for h in hits:
            klass = int(h) - self.layout.name
            if klass <= 0:
                continue
            try:
                ns, nm = self.class_name(klass)
            except ProcessError:
                continue
            if nm != name:
                continue
            if namespace is not None and ns != namespace:
                continue
            out.append(klass)
        return out

    def _scan_ascii(
        self, pattern: bytes, progress=None, lo: int = 0, hi: int = 1 << 63
    ) -> list[int]:
        out: list[int] = []
        regions = [r for r in self.proc.regions() if r.end > lo and r.base < hi]
        for i, region in enumerate(regions):
            if progress:
                progress(i, len(regions))
            for base, data in self.proc.read_chunks(region):
                pos = data.find(pattern)
                while pos != -1:
                    # Reject matches that are the tail of a longer identifier.
                    if pos == 0 or data[pos - 1] not in _IDENT_BYTES:
                        out.append(base + pos)
                    pos = data.find(pattern, pos + 1)
        if progress:
            progress(len(regions), len(regions))
        return out

    # --- finding instances -------------------------------------------------
    def find_instances(
        self, klass: int, near: int = 512 << 20, progress=None
    ) -> np.ndarray:
        """Every live object whose class is `klass`.

        Objects of a type all share one vtable, so this finds the vtables
        pointing at the class, then the objects pointing at those vtables.

        The vtable lives in the same domain pool as the class, so that first
        sweep is windowed. The objects themselves are in the GC heap, which
        can be anywhere, so the second sweep covers everything.
        """
        vtables = self._scan_pointers_to(
            {klass}, progress, lo=max(0, klass - near), hi=klass + near
        )
        if not len(vtables):
            vtables = self._scan_pointers_to({klass}, progress)
        if not len(vtables):
            return np.empty(0, np.uint64)
        # A vtable is pointed at by its class too; keep only real candidates.
        targets = {int(v) for v in vtables if int(v) != klass}
        if not targets:
            return np.empty(0, np.uint64)
        objects = self._scan_pointers_to(targets, progress, regions=self.cached_heap_regions())
        # Every hit is an address *holding* a vtable pointer. For a real
        # object that address is the object itself, so verify the round trip.
        return np.array(
            [o for o in objects.tolist() if self._points_to_class(int(o), klass)],
            dtype=np.uint64,
        )

    def _points_to_class(self, obj: int, klass: int) -> bool:
        try:
            return self.class_of(obj) == klass
        except (MonoError, ProcessError):
            return False

    def _scan_pointers_to(
        self,
        targets: set[int],
        progress=None,
        lo: int = 0,
        hi: int = 1 << 63,
        regions: list | None = None,
    ) -> np.ndarray:
        """Addresses holding a pointer to any value in `targets`.

        `lo`/`hi` bound which regions are searched, and `regions` supplies a
        pre-narrowed list outright (see `heap_regions`). Between them this is
        what makes a lookup take seconds instead of minutes.
        """
        wanted = np.array(sorted(targets), dtype=np.uint64)
        hits: list[np.ndarray] = []
        if regions is None:
            regions = [
                r
                for r in self.proc.regions(writable_only=True)
                if r.type_name == "private"
            ]
        regions = [r for r in regions if r.end > lo and r.base < hi]
        for i, region in enumerate(regions):
            if progress:
                progress(i, len(regions))
            for base, data in self.proc.read_chunks(region):
                n = len(data) // 8
                if not n:
                    continue
                arr = np.frombuffer(data, dtype=np.uint64, count=n)
                mask = np.isin(arr, wanted)
                idx = np.flatnonzero(mask)
                if len(idx):
                    hits.append(base + idx.astype(np.uint64) * np.uint64(8))
        if progress:
            progress(len(regions), len(regions))
        return np.unique(np.concatenate(hits)) if hits else np.empty(0, np.uint64)

    # --- session cache -----------------------------------------------------
    def save_cache(self, path) -> None:
        """Remember what was expensive to find, for this process only.

        Bootstrapping and class lookup each take a minute of sweeping, but
        the answers hold for the life of the process (Boehm never moves
        objects). Caching them makes a rerun start instantly.
        """
        import json
        from pathlib import Path as _Path

        if self.layout is None:
            return
        data = {
            "pid": self.proc.pid,
            "layout": {"name": self.layout.name, "name_space": self.layout.name_space},
            "classes": {k: v for k, v in self._class_cache.items()},
            "name_heap_hint": self._name_heap_hint,
            "vtable_hint": self._vtable_hint,
        }
        _Path(path).write_text(json.dumps(data, indent=2), encoding="utf-8")

    def load_cache(self, path) -> bool:
        """Restore a cache written by this same process. Returns success.

        Every cached value is re-verified before use: a PID can be reused,
        and a stale MonoClass pointer would otherwise produce silent
        nonsense rather than an error.
        """
        import json
        from pathlib import Path as _Path

        p = _Path(path)
        if not p.exists():
            return False
        try:
            data = json.loads(p.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError):
            return False
        if data.get("pid") != self.proc.pid:
            return False
        lay = data.get("layout") or {}
        if "name" not in lay or "name_space" not in lay:
            return False
        self.layout = ClassLayout(lay["name"], lay["name_space"])
        self._name_heap_hint = data.get("name_heap_hint")
        self._vtable_hint = data.get("vtable_hint")
        restored: dict[str, int] = {}
        for key, addr in (data.get("classes") or {}).items():
            want = key.rsplit(".", 1)[-1]
            try:
                _, name = self.class_name(int(addr))
            except (ProcessError, MonoError):
                continue
            if name == want:
                restored[key] = int(addr)
        self._class_cache = restored
        return True

    def find_class_cached(self, name: str, namespace: str | None = None, progress=None):
        """find_class(), but remembering the answer for this session."""
        key = f"{namespace}.{name}" if namespace else name
        if key in self._class_cache:
            return [self._class_cache[key]]
        hits = self.find_class(name, namespace, progress=progress)
        if hits:
            self._class_cache[key] = hits[0]
        return hits

    # --- locating the managed heap ----------------------------------------
    def heap_regions(
        self,
        pages: int = 3,
        page: int = 4096,
        min_density: float = 0.05,
        confirm: bool = True,
    ):
        """Private regions that plausibly contain managed objects.

        The process maps ~20 GB, but Mono's objects live in a small slice of
        it. Sweeping only that slice turns a four-minute search into seconds.

        Two stages, cheapest first: sample a few pages per region and keep
        those with a decent share of pointer-shaped qwords, then confirm by
        actually walking one object's vtable to a class name. Validated
        vtables are cached, so the confirm stage costs almost nothing after
        the first few regions.

        This is an optimisation, never a filter of record - callers fall
        back to sweeping everything if it turns up nothing.
        """
        if confirm and self.layout is None:
            raise MonoError("call bootstrap() or discover() first")
        out = []
        for r in self.proc.regions(writable_only=True):
            if r.type_name != "private":
                continue
            if self._region_has_objects(r, pages, page, min_density, confirm):
                out.append(r)
        return out

    def candidate_regions(self):
        """Regions that might hold managed data, on density alone.

        Usable before the class layout is known, which is what lets the
        bootstrap scan skip most of the address space.
        """
        if self._candidate_regions is None:
            self._candidate_regions = self.heap_regions(confirm=False)
        return self._candidate_regions

    def cached_heap_regions(self):
        """heap_regions(), computed once per session.

        Regions are stable for the life of the process, and the detection
        pass costs a few seconds, so there is no reason to repeat it.
        """
        if self._heap_regions is None:
            found = self.heap_regions()
            # Never let an optimisation lose objects: if detection came back
            # suspiciously empty, fall back to sweeping everything.
            if not found:
                found = [
                    r
                    for r in self.proc.regions(writable_only=True)
                    if r.type_name == "private"
                ]
            self._heap_regions = found
        return self._heap_regions

    def _region_has_objects(
        self, region, pages: int, page: int, min_density: float, confirm: bool = True
    ) -> bool:
        step = max(page, region.size // max(pages, 1))
        for off in range(0, region.size, step):
            data = self.proc.try_read(region.base + off, min(page, region.size - off))
            if not data or len(data) < 64:
                continue
            arr = np.frombuffer(data, dtype=np.uint64, count=len(data) // 8)
            ptrish = arr[(arr > 0x10000) & (arr < 0x7FFFFFFFFFFF) & (arr % 8 == 0)]
            if len(ptrish) < max(4, int(len(arr) * min_density)):
                continue
            if not confirm:
                return True
            # Cheap stage passed; confirm one of them really is a vtable.
            for v in ptrish[:24].tolist():
                if v in self._vtable_cache:
                    return True
                if v in self._not_vtable:
                    continue
                if self._is_vtable(v):
                    self._vtable_cache.add(v)
                    return True
                self._not_vtable.add(v)
        return False

    def _is_vtable(self, vtable: int) -> bool:
        klass = self.proc.try_read(vtable, 8)
        if not klass:
            return False
        k = int.from_bytes(klass, "little")
        if not self._plausible_pointer(k):
            return False
        assert self.layout is not None
        name_ptr = self.proc.try_read(k + self.layout.name, 8)
        if not name_ptr:
            return False
        return self._ascii_at(int.from_bytes(name_ptr, "little")) is not None

    # --- managed containers ------------------------------------------------
    def read_array(self, addr: int, max_items: int = 65536) -> list[int]:
        """Elements of a managed reference array (T[] of object pointers)."""
        if not addr:
            return []
        count = self.proc.i64(addr + MONO_ARRAY_COUNT)
        if not 0 <= count <= max_items:
            raise MonoError(f"implausible array length {count} at 0x{addr:X}")
        if count == 0:
            return []
        raw = self.proc.read(addr + MONO_ARRAY_DATA, count * 8)
        return np.frombuffer(raw, dtype=np.uint64, count=count).tolist()

    def read_list(self, addr: int, max_items: int = 65536) -> list[int]:
        """Elements of a List<T> of reference type, honouring _size.

        The backing array is usually longer than the list; _size is the
        count that actually matters.
        """
        if not addr:
            return []
        items = self.proc.ptr(addr + LIST_ITEMS)
        size = self.proc.i32(addr + LIST_SIZE)
        if not 0 <= size <= max_items:
            raise MonoError(f"implausible list count {size} at 0x{addr:X}")
        if not items or size == 0:
            return []
        backing = self.read_array(items, max_items)
        return backing[:size]

    def read_datetime(self, addr: int):
        """A System.DateTime field as a Python datetime (UTC-naive).

        DateTime packs its kind into the top two bits of _dateData; the rest
        is ticks of 100ns since 0001-01-01.
        """
        import datetime as _dt

        data = self.proc.u64(addr)
        ticks = data & 0x3FFFFFFFFFFFFFFF
        try:
            return _dt.datetime(1, 1, 1) + _dt.timedelta(microseconds=ticks // 10)
        except (OverflowError, ValueError):
            return None

    def read_guid(self, addr: int) -> str:
        import uuid

        raw = self.proc.read(addr, 16)
        return str(uuid.UUID(bytes_le=raw))

    def read_string_field(self, addr: int) -> str | None:
        """Follow a string-typed field and read it, tolerating null."""
        ptr = self.proc.ptr(addr)
        if not ptr:
            return None
        try:
            return self.proc.mono_string(ptr)
        except ProcessError:
            return None

    # --- reading a typed object -------------------------------------------
    def read_fields(self, obj: int, info: TypeInfo) -> list[tuple[str, str, int | None, object]]:
        """Read an object through a metadata type description.

        Returns (field name, type name, offset, value) rows. Values are
        decoded for primitives and strings; reference fields come back as
        addresses. Offsets are the predicted ones - compare the output
        against what you expect to confirm the layout is right.
        """
        rows: list[tuple[str, str, int | None, object]] = []
        for f in info.instance_fields:
            off = f.predicted_offset
            if off is None:
                rows.append((f.name, f.type_name, None, "<unknown offset>"))
                continue
            addr = obj + off
            value: object
            try:
                if f.scan_type:
                    value = self._read_scalar(addr, f.scan_type)
                elif f.type_name == "string":
                    ptr = self.proc.ptr(addr)
                    value = repr(self.proc.mono_string(ptr)) if ptr else None
                else:
                    ptr = self.proc.ptr(addr)
                    named = self.identify(ptr) if ptr else ""
                    value = f"0x{ptr:X}" + (f" ({named})" if named else "")
            except ProcessError as exc:
                value = f"<{exc}>"
            rows.append((f.name, f.type_name, off, value))
        return rows

    def _read_scalar(self, addr: int, scan_type: str):
        reader = {
            "i8": self.proc.i8, "u8": self.proc.u8,
            "i16": self.proc.i16, "u16": self.proc.u16,
            "i32": self.proc.i32, "u32": self.proc.u32,
            "i64": self.proc.i64, "u64": self.proc.u64,
            "f32": self.proc.f32, "f64": self.proc.f64,
        }[scan_type]
        return reader(addr)

    def object_size_hint(self, obj: int) -> int:
        """Rough object extent, for hexdump framing."""
        return MONO_OBJ_HEADER + 0x80

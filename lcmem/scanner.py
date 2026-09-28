"""Value scanning: the find-then-narrow workflow of a memory editor.

Typical use::

    s = Scanner(proc, "f32")
    s.first("exact", 100.0)      # while the value reads 100
    # ...change it in game...
    s.refine("exact", 75.0)      # narrow to addresses that now read 75
    s.refine("decreased")        # or narrow without knowing the number

Two internal shapes back this:

* sparse hits    - (addresses, values), produced by any value-based scan
* dense snapshot - whole regions kept as arrays, so an "unknown initial
  value" hunt can later ask what *changed*

Dense snapshots are memory-hungry, so they are budgeted (see `first_unknown`).

Every scan reduces to one primitive, `_slots`, which turns a block of bytes
into the array of candidate values it contains at the configured alignment.
Slot *i* of a block read from address A always lives at ``A + i * stride``,
which keeps address arithmetic uniform everywhere else in this file.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Callable, Iterator

import numpy as np

from .process import Process, ProcessError, Region

TYPES: dict[str, np.dtype] = {
    "i8": np.dtype(np.int8),
    "u8": np.dtype(np.uint8),
    "i16": np.dtype(np.int16),
    "u16": np.dtype(np.uint16),
    "i32": np.dtype(np.int32),
    "u32": np.dtype(np.uint32),
    "i64": np.dtype(np.int64),
    "u64": np.dtype(np.uint64),
    "f32": np.dtype(np.float32),
    "f64": np.dtype(np.float64),
}

FLOAT_TYPES = {"f32", "f64"}

VALUE_MODES = {"exact", "not", "greater", "less", "between"}
PREV_VALUE_MODES = {"increased_by", "decreased_by"}
DELTA_MODES = {"changed", "unchanged", "increased", "decreased"}
ALL_MODES = VALUE_MODES | PREV_VALUE_MODES | DELTA_MODES

#: bytes per ReadProcessMemory call while sweeping a region
CHUNK = 8 * 1024 * 1024


class ScanError(RuntimeError):
    pass


@dataclass
class Snapshot:
    """A dense capture of one contiguous block's values."""

    base: int
    values: np.ndarray
    stride: int


@dataclass
class Scanner:
    proc: Process
    type: str = "i32"
    #: address stride between candidates; defaults to the type's own size
    align: int | None = None
    #: skip regions larger than this during scans (bytes); None = no limit
    max_region: int | None = 256 * 1024 * 1024
    #: only scan private+writable memory - where mutable game state lives
    private_only: bool = True
    writable_only: bool = True
    #: absolute tolerance for float "exact" matches
    tolerance: float = 1e-4
    progress: Callable[[int, int], None] | None = None

    addresses: np.ndarray = field(default_factory=lambda: np.empty(0, np.uint64))
    values: np.ndarray = field(default_factory=lambda: np.empty(0, np.int32))
    _snapshots: list[Snapshot] = field(default_factory=list)

    def __post_init__(self) -> None:
        if self.type not in TYPES:
            raise ScanError(f"unknown type {self.type!r}; pick from {sorted(TYPES)}")
        if self.align is not None and self.align < 1:
            raise ScanError("align must be >= 1")
        self.values = np.empty(0, self.dtype)

    # --- basics ------------------------------------------------------------
    @property
    def dtype(self) -> np.dtype:
        return TYPES[self.type]

    @property
    def item(self) -> int:
        return self.dtype.itemsize

    @property
    def stride(self) -> int:
        return self.align or self.item

    @property
    def count(self) -> int:
        if self._snapshots:
            return int(sum(len(s.values) for s in self._snapshots))
        return len(self.addresses)

    @property
    def is_snapshot(self) -> bool:
        return bool(self._snapshots)

    def reset(self) -> None:
        self.addresses = np.empty(0, np.uint64)
        self.values = np.empty(0, self.dtype)
        self._snapshots = []

    def _regions(self) -> list[Region]:
        out = []
        for r in self.proc.regions(writable_only=self.writable_only):
            if self.private_only and r.type_name != "private":
                continue
            if self.max_region is not None and r.size > self.max_region:
                continue
            out.append(r)
        return out

    def _cast(self, value) -> np.generic:
        """Coerce a Python number into the scan dtype, rejecting overflow."""
        if value is None:
            raise ScanError("this scan mode requires a value")
        if self.type in FLOAT_TYPES:
            return self.dtype.type(float(value))
        v = int(value)
        info = np.iinfo(self.dtype)
        if not info.min <= v <= info.max:
            raise ScanError(f"{v} does not fit in {self.type} ({info.min}..{info.max})")
        return self.dtype.type(v)

    # --- the one primitive -------------------------------------------------
    def _slots(self, data: bytes) -> np.ndarray:
        """Values contained in `data` at the configured stride.

        Slot i corresponds to byte offset ``i * stride``.
        """
        item, step = self.item, self.stride
        if step == item:
            return np.frombuffer(data, dtype=self.dtype, count=len(data) // item)
        buf = np.frombuffer(data, dtype=np.uint8)
        n = (len(buf) - item) // step + 1
        if n <= 0:
            return np.empty(0, self.dtype)
        mat = np.lib.stride_tricks.as_strided(buf, shape=(n, item), strides=(step, 1))
        return np.ascontiguousarray(mat).view(self.dtype).ravel()

    def _blocks(self, region: Region) -> Iterator[tuple[int, bytes]]:
        """Read a region in chunks that overlap enough to not miss a value
        straddling a chunk boundary. Overlap can yield duplicate addresses,
        which callers drop with `np.unique`."""
        overlap = self.item - 1
        off = 0
        while off < region.size:
            n = min(CHUNK, region.size - off)
            want = min(n + overlap, region.size - off)
            # read_span degrades to page granularity rather than dropping the
            # whole chunk when one page inside it is inaccessible.
            yield from self.proc.read_span(region.base + off, want)
            off += n

    # --- first scan --------------------------------------------------------
    def first(self, mode: str = "exact", value=None, value2=None) -> int:
        """Scan all eligible memory for a value. Returns the hit count."""
        if mode not in VALUE_MODES:
            raise ScanError(
                f"a first scan needs a value-based mode {sorted(VALUE_MODES)}, not {mode!r}"
            )
        self.reset()
        regions = self._regions()
        addr_parts: list[np.ndarray] = []
        val_parts: list[np.ndarray] = []

        for i, region in enumerate(regions):
            if self.progress:
                self.progress(i, len(regions))
            for base, data in self._blocks(region):
                arr = self._slots(data)
                if not len(arr):
                    continue
                mask = self._compare(arr, mode, value, value2)
                idx = np.flatnonzero(mask)
                if not len(idx):
                    continue
                addr_parts.append(base + idx.astype(np.uint64) * np.uint64(self.stride))
                val_parts.append(arr[idx])
        if self.progress:
            self.progress(len(regions), len(regions))

        if addr_parts:
            addrs = np.concatenate(addr_parts)
            vals = np.concatenate(val_parts)
            addrs, keep = np.unique(addrs, return_index=True)
            self.addresses = addrs
            self.values = vals[keep]
        return self.count

    def first_unknown(self, budget_mb: int = 1024) -> int:
        """Snapshot memory without knowing the value, for later delta scans.

        `budget_mb` caps how much is captured, so a 20 GB address space
        cannot exhaust RAM. Regions are taken until the budget runs out.
        """
        self.reset()
        regions = self._regions()
        budget = budget_mb * 1024 * 1024
        used = 0
        for i, region in enumerate(regions):
            if self.progress:
                self.progress(i, len(regions))
            if used + region.size > budget:
                continue
            off = 0
            while off < region.size:
                n = min(CHUNK, region.size - off)
                data = self.proc.try_read(region.base + off, n)
                if data:
                    arr = self._slots(data)
                    if len(arr):
                        self._snapshots.append(
                            Snapshot(region.base + off, arr.copy(), self.stride)
                        )
                        used += arr.nbytes
                off += n
        if self.progress:
            self.progress(len(regions), len(regions))
        return self.count

    # --- refine ------------------------------------------------------------
    def refine(self, mode: str = "exact", value=None, value2=None) -> int:
        """Narrow the current result set. Returns the surviving hit count."""
        if mode not in ALL_MODES:
            raise ScanError(f"unknown mode {mode!r}; pick from {sorted(ALL_MODES)}")
        if self.is_snapshot:
            return self._refine_snapshot(mode, value, value2)
        if not len(self.addresses):
            raise ScanError("nothing to refine - run a first scan")
        return self._refine_hits(mode, value, value2)

    def _refine_snapshot(self, mode: str, value, value2) -> int:
        addr_parts: list[np.ndarray] = []
        val_parts: list[np.ndarray] = []
        total = len(self._snapshots)
        for i, snap in enumerate(self._snapshots):
            if self.progress:
                self.progress(i, total)
            span = (len(snap.values) - 1) * snap.stride + self.item
            data = self.proc.try_read(snap.base, span)
            if not data:
                continue
            cur = self._slots(data)
            n = min(len(cur), len(snap.values))
            if not n:
                continue
            cur, old = cur[:n], snap.values[:n]
            mask = self._compare(cur, mode, value, value2, previous=old)
            idx = np.flatnonzero(mask)
            if not len(idx):
                continue
            addr_parts.append(snap.base + idx.astype(np.uint64) * np.uint64(snap.stride))
            val_parts.append(cur[idx])
        if self.progress:
            self.progress(total, total)

        self._snapshots = []
        self.addresses = np.concatenate(addr_parts) if addr_parts else np.empty(0, np.uint64)
        self.values = np.concatenate(val_parts) if val_parts else np.empty(0, self.dtype)
        return self.count

    def _refine_hits(self, mode: str, value, value2) -> int:
        cur, ok = self.read_values(self.addresses)
        mask = np.zeros(len(self.addresses), bool)
        if ok.any():
            mask[ok] = self._compare(cur[ok], mode, value, value2, previous=self.values[ok])
        self.addresses = self.addresses[mask]
        self.values = cur[mask]
        return self.count

    # --- comparison --------------------------------------------------------
    def _compare(
        self, arr: np.ndarray, mode: str, value, value2, previous: np.ndarray | None = None
    ) -> np.ndarray:
        if (mode in DELTA_MODES or mode in PREV_VALUE_MODES) and previous is None:
            raise ScanError(f"mode {mode!r} needs a previous scan to compare against")
        # Arbitrary bytes read as floats are full of NaNs and denormals; that
        # is expected here, so don't let numpy warn about every comparison.
        with np.errstate(invalid="ignore", over="ignore", under="ignore"):
            return self._compare_inner(arr, mode, value, value2, previous)

    def _compare_inner(
        self, arr: np.ndarray, mode: str, value, value2, previous: np.ndarray | None
    ) -> np.ndarray:
        if mode == "exact":
            v = self._cast(value)
            if self.type in FLOAT_TYPES:
                return np.abs(arr.astype(np.float64) - float(v)) <= self.tolerance
            return arr == v
        if mode == "not":
            v = self._cast(value)
            if self.type in FLOAT_TYPES:
                return np.abs(arr.astype(np.float64) - float(v)) > self.tolerance
            return arr != v
        if mode == "greater":
            return arr > self._cast(value)
        if mode == "less":
            return arr < self._cast(value)
        if mode == "between":
            lo, hi = self._cast(value), self._cast(value2)
            return (arr >= lo) & (arr <= hi)
        if mode == "changed":
            return arr != previous
        if mode == "unchanged":
            return arr == previous
        if mode == "increased":
            return arr > previous
        if mode == "decreased":
            return arr < previous
        # Deltas are computed in a wider signed type so they cannot wrap.
        if mode in PREV_VALUE_MODES:
            delta = float(value) if self.type in FLOAT_TYPES else int(value)
            wide = np.float64 if self.type in FLOAT_TYPES else np.int64
            diff = arr.astype(wide) - previous.astype(wide)
            want = delta if mode == "increased_by" else -delta
            if self.type in FLOAT_TYPES:
                return np.abs(diff - want) <= self.tolerance
            return diff == want
        raise ScanError(f"unhandled mode {mode!r}")

    # --- batched reads -----------------------------------------------------
    def read_values(
        self, addresses: np.ndarray, window: int = 64 * 1024
    ) -> tuple[np.ndarray, np.ndarray]:
        """Read many scattered addresses with as few syscalls as possible.

        Nearby addresses are coalesced into one read. Returns (values, ok_mask)
        aligned with `addresses`.
        """
        out = np.zeros(len(addresses), self.dtype)
        ok = np.zeros(len(addresses), bool)
        if not len(addresses):
            return out, ok

        order = np.argsort(addresses, kind="stable")
        srt = addresses[order]
        i, n = 0, len(srt)
        while i < n:
            start = int(srt[i])
            j = i
            while j + 1 < n and int(srt[j + 1]) - start + self.item <= window:
                j += 1
            span = int(srt[j]) - start + self.item
            data = self.proc.try_read(start, span)
            if data and len(data) >= span:
                buf = np.frombuffer(data, dtype=np.uint8)
                offs = (srt[i : j + 1] - np.uint64(start)).astype(np.int64)
                # Gather each value's bytes in one shot, then reinterpret.
                gather = buf[offs[:, None] + np.arange(self.item)]
                vals = np.ascontiguousarray(gather).view(self.dtype).ravel()
                out[order[i : j + 1]] = vals
                ok[order[i : j + 1]] = True
            i = j + 1
        return out, ok

    # --- output ------------------------------------------------------------
    def results(self, limit: int = 50) -> list[tuple[int, object]]:
        """Current hits as (address, freshly-read value); None if unreadable."""
        sel = self.addresses[:limit]
        vals, ok = self.read_values(sel)
        return [
            (int(a), (v.item() if o else None)) for a, v, o in zip(sel, vals, ok)
        ]

    def write_all(self, value, limit: int | None = None) -> int:
        """Write `value` to every hit. Returns how many writes succeeded."""
        raw = np.asarray([self._cast(value)], dtype=self.dtype).tobytes()
        targets = self.addresses if limit is None else self.addresses[:limit]
        done = 0
        for a in targets:
            try:
                self.proc.write(int(a), raw)
                done += 1
            except ProcessError:
                pass
        return done

    def __repr__(self) -> str:
        kind = "snapshot" if self.is_snapshot else "hits"
        return f"<Scanner {self.type} {kind}={self.count}>"

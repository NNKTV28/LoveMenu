"""Attach to a running process and read/write its memory."""
from __future__ import annotations

import ctypes
import struct
from ctypes import wintypes
from dataclasses import dataclass
from typing import Iterator

from . import win32 as w

# Mono / .NET object layout on x64 (Unity's MonoBleedingEdge runtime).
MONO_OBJ_HEADER = 0x10          # vtable pointer + sync block
MONO_STRING_LENGTH = 0x10       # int32 char count
MONO_STRING_CHARS = 0x14        # start of UTF-16 data
MONO_ARRAY_COUNT = 0x18         # native-int element count
MONO_ARRAY_DATA = 0x20          # first element

CHUNK = 4 * 1024 * 1024
PAGE = 4096


class ProcessError(RuntimeError):
    pass


@dataclass(frozen=True)
class Module:
    name: str
    base: int
    size: int
    path: str

    @property
    def end(self) -> int:
        return self.base + self.size

    def __contains__(self, addr: int) -> bool:
        return self.base <= addr < self.end

    def __repr__(self) -> str:
        return f"<Module {self.name} @ 0x{self.base:X} +0x{self.size:X}>"


@dataclass(frozen=True)
class Region:
    base: int
    size: int
    protect: int
    type: int

    @property
    def end(self) -> int:
        return self.base + self.size

    @property
    def writable(self) -> bool:
        return bool(self.protect & w.WRITABLE)

    @property
    def type_name(self) -> str:
        return {w.MEM_PRIVATE: "private", w.MEM_MAPPED: "mapped", w.MEM_IMAGE: "image"}.get(
            self.type, "?"
        )

    def __repr__(self) -> str:
        return f"<Region 0x{self.base:X} +0x{self.size:X} {self.type_name}>"


def find_pids(name: str) -> list[int]:
    """All PIDs whose executable name matches `name` (case-insensitive)."""
    if not name.lower().endswith(".exe"):
        name += ".exe"
    want = name.lower()
    snap = w.kernel32.CreateToolhelp32Snapshot(w.TH32CS_SNAPPROCESS, 0)
    if snap == w.INVALID_HANDLE_VALUE:
        raise ProcessError("CreateToolhelp32Snapshot failed")
    try:
        entry = w.PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        out: list[int] = []
        ok = w.kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.lower() == want:
                out.append(entry.th32ProcessID)
            ok = w.kernel32.Process32NextW(snap, ctypes.byref(entry))
        return out
    finally:
        w.kernel32.CloseHandle(snap)


class Process:
    """A handle to another process, with typed memory access."""

    def __init__(self, pid: int, write: bool = True):
        w.enable_debug_privilege()
        self.pid = pid
        access = w.RW_ACCESS if write else w.READ_ACCESS
        handle = w.kernel32.OpenProcess(access, False, pid)
        if not handle and write:
            # Fall back to read-only rather than failing outright.
            handle = w.kernel32.OpenProcess(w.READ_ACCESS, False, pid)
            access = w.READ_ACCESS
        if not handle:
            err = ctypes.get_last_error()
            raise ProcessError(
                f"OpenProcess({pid}) failed (error {err}). "
                "Try running this from an elevated terminal."
            )
        self.handle = handle
        self.can_write = bool(access & w.PROCESS_VM_WRITE)
        self._modules: dict[str, Module] | None = None

    @classmethod
    def attach(cls, target: str | int, write: bool = True) -> "Process":
        if isinstance(target, int):
            return cls(target, write)
        pids = find_pids(target)
        if not pids:
            raise ProcessError(f"no running process named {target!r}")
        if len(pids) > 1:
            raise ProcessError(f"{target!r} is ambiguous: PIDs {pids}. Pass one explicitly.")
        return cls(pids[0], write)

    def close(self) -> None:
        if getattr(self, "handle", None):
            w.kernel32.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self) -> "Process":
        return self

    def __exit__(self, *exc) -> None:
        self.close()

    @property
    def is_64bit(self) -> bool:
        wow64 = wintypes.BOOL()
        w.kernel32.IsWow64Process(self.handle, ctypes.byref(wow64))
        return not wow64.value

    @property
    def ptr_size(self) -> int:
        return 8 if self.is_64bit else 4

    @property
    def alive(self) -> bool:
        buf = w.MEMORY_BASIC_INFORMATION64()
        return bool(
            w.kernel32.VirtualQueryEx(self.handle, 0, ctypes.byref(buf), ctypes.sizeof(buf))
        )

    # --- modules -----------------------------------------------------------
    @property
    def modules(self) -> dict[str, Module]:
        if self._modules is None:
            self._modules = self._load_modules()
        return self._modules

    def refresh_modules(self) -> dict[str, Module]:
        self._modules = self._load_modules()
        return self._modules

    def _load_modules(self) -> dict[str, Module]:
        flags = w.TH32CS_SNAPMODULE | w.TH32CS_SNAPMODULE32
        snap = w.kernel32.CreateToolhelp32Snapshot(flags, self.pid)
        if snap == w.INVALID_HANDLE_VALUE:
            raise ProcessError("module snapshot failed")
        try:
            entry = w.MODULEENTRY32W()
            entry.dwSize = ctypes.sizeof(entry)
            mods: dict[str, Module] = {}
            ok = w.kernel32.Module32FirstW(snap, ctypes.byref(entry))
            while ok:
                base = ctypes.cast(entry.modBaseAddr, ctypes.c_void_p).value or 0
                mods[entry.szModule.lower()] = Module(
                    entry.szModule, base, entry.modBaseSize, entry.szExePath
                )
                ok = w.kernel32.Module32NextW(snap, ctypes.byref(entry))
            return mods
        finally:
            w.kernel32.CloseHandle(snap)

    def module(self, name: str) -> Module:
        mod = self.modules.get(name.lower())
        if mod is None:
            raise ProcessError(f"module {name!r} not loaded in PID {self.pid}")
        return mod

    def describe(self, addr: int) -> str:
        """Render an address as module+offset when it lands inside one."""
        for mod in self.modules.values():
            if addr in mod:
                return f"{mod.name}+0x{addr - mod.base:X}"
        return f"0x{addr:X}"

    # --- regions -----------------------------------------------------------
    def regions(
        self,
        writable_only: bool = False,
        types: tuple[int, ...] = (w.MEM_PRIVATE, w.MEM_IMAGE, w.MEM_MAPPED),
        max_addr: int = 0x7FFFFFFFFFFF,
    ) -> Iterator[Region]:
        addr = 0
        info = w.MEMORY_BASIC_INFORMATION64()
        size = ctypes.sizeof(info)
        while addr < max_addr:
            if not w.kernel32.VirtualQueryEx(self.handle, addr, ctypes.byref(info), size):
                break
            nxt = info.BaseAddress + info.RegionSize
            if nxt <= addr:
                break
            if (
                info.State == w.MEM_COMMIT
                and info.Type in types
                and info.Protect & w.READABLE
                and not info.Protect & w.PAGE_GUARD
            ):
                if not writable_only or info.Protect & w.WRITABLE:
                    yield Region(info.BaseAddress, info.RegionSize, info.Protect, info.Type)
            addr = nxt

    # --- raw access --------------------------------------------------------
    def read(self, addr: int, size: int) -> bytes:
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t()
        if not w.kernel32.ReadProcessMemory(self.handle, addr, buf, size, ctypes.byref(got)):
            raise ProcessError(
                f"read 0x{addr:X}+{size} failed (error {ctypes.get_last_error()})"
            )
        return buf.raw[: got.value]

    def try_read(self, addr: int, size: int) -> bytes | None:
        """Like read(), but returns None instead of raising on failure."""
        buf = ctypes.create_string_buffer(size)
        got = ctypes.c_size_t()
        if not w.kernel32.ReadProcessMemory(self.handle, addr, buf, size, ctypes.byref(got)):
            return None
        return buf.raw[: got.value]

    def write(self, addr: int, data: bytes) -> int:
        if not self.can_write:
            raise ProcessError("process was opened read-only")
        written = ctypes.c_size_t()
        ok = w.kernel32.WriteProcessMemory(
            self.handle, addr, data, len(data), ctypes.byref(written)
        )
        if not ok:
            # Page may be read-only; flip protection, write, flip it back.
            old = wintypes.DWORD()
            if not w.kernel32.VirtualProtectEx(
                self.handle, addr, len(data), w.PAGE_EXECUTE_READWRITE, ctypes.byref(old)
            ):
                raise ProcessError(
                    f"write 0x{addr:X} failed (error {ctypes.get_last_error()})"
                )
            try:
                ok = w.kernel32.WriteProcessMemory(
                    self.handle, addr, data, len(data), ctypes.byref(written)
                )
            finally:
                tmp = wintypes.DWORD()
                w.kernel32.VirtualProtectEx(
                    self.handle, addr, len(data), old.value, ctypes.byref(tmp)
                )
            if not ok:
                raise ProcessError(
                    f"write 0x{addr:X} failed (error {ctypes.get_last_error()})"
                )
        return written.value

    def read_span(self, addr: int, size: int) -> Iterator[tuple[int, bytes]]:
        """Yield the readable pieces of a span.

        ReadProcessMemory is all-or-nothing: if a single page inside the
        requested range is inaccessible, the whole call fails. Taking that
        at face value means one bad page hides megabytes of memory - which
        is how a class lookup can miss a name that is really there. So on
        failure, split and retry, down to page granularity.
        """
        data = self.try_read(addr, size)
        if data:
            yield addr, data
            return
        if size <= PAGE:
            return
        half = ((size // 2) + PAGE - 1) & ~(PAGE - 1)
        if half <= 0 or half >= size:
            return
        yield from self.read_span(addr, half)
        yield from self.read_span(addr + half, size - half)

    def read_chunks(self, region: Region, chunk: int = CHUNK) -> Iterator[tuple[int, bytes]]:
        """Yield (address, data) over a region, skipping unreadable pages."""
        off = 0
        while off < region.size:
            n = min(chunk, region.size - off)
            yield from self.read_span(region.base + off, n)
            off += n

    # --- typed access ------------------------------------------------------
    def _get(self, addr: int, fmt: str):
        return struct.unpack(fmt, self.read(addr, struct.calcsize(fmt)))[0]

    def _put(self, addr: int, fmt: str, value) -> int:
        return self.write(addr, struct.pack(fmt, value))

    def i8(self, a: int) -> int:
        return self._get(a, "<b")

    def u8(self, a: int) -> int:
        return self._get(a, "<B")

    def i16(self, a: int) -> int:
        return self._get(a, "<h")

    def u16(self, a: int) -> int:
        return self._get(a, "<H")

    def i32(self, a: int) -> int:
        return self._get(a, "<i")

    def u32(self, a: int) -> int:
        return self._get(a, "<I")

    def i64(self, a: int) -> int:
        return self._get(a, "<q")

    def u64(self, a: int) -> int:
        return self._get(a, "<Q")

    def f32(self, a: int) -> float:
        return self._get(a, "<f")

    def f64(self, a: int) -> float:
        return self._get(a, "<d")

    def boolean(self, a: int) -> bool:
        return bool(self._get(a, "<B"))

    def set_i32(self, a: int, v: int) -> int:
        return self._put(a, "<i", v)

    def set_u32(self, a: int, v: int) -> int:
        return self._put(a, "<I", v)

    def set_i64(self, a: int, v: int) -> int:
        return self._put(a, "<q", v)

    def set_u64(self, a: int, v: int) -> int:
        return self._put(a, "<Q", v)

    def set_f32(self, a: int, v: float) -> int:
        return self._put(a, "<f", v)

    def set_f64(self, a: int, v: float) -> int:
        return self._put(a, "<d", v)

    def set_u8(self, a: int, v: int) -> int:
        return self._put(a, "<B", v)

    def set_boolean(self, a: int, v: bool) -> int:
        return self._put(a, "<B", 1 if v else 0)

    def ptr(self, addr: int) -> int:
        return self.u64(addr) if self.ptr_size == 8 else self.u32(addr)

    def chain(self, base: int, *offsets: int) -> int:
        """Walk a pointer chain, dereferencing at each step.

        chain(base, 0x10, 0x28, 0x4C) reads *(base+0x10), then *(that+0x28),
        and returns that+0x4C without dereferencing the last hop.
        """
        addr = base
        for off in offsets[:-1]:
            addr = self.ptr(addr + off)
            if not addr:
                raise ProcessError(f"null pointer while resolving chain at +0x{off:X}")
        return addr + (offsets[-1] if offsets else 0)

    # --- Mono helpers ------------------------------------------------------
    def mono_string(self, addr: int, max_chars: int = 4096) -> str:
        """Read a System.String object; addr is the object pointer."""
        if not addr:
            return ""
        length = self.i32(addr + MONO_STRING_LENGTH)
        if not 0 <= length <= max_chars:
            raise ProcessError(f"implausible string length {length} at 0x{addr:X}")
        if length == 0:
            return ""
        raw = self.read(addr + MONO_STRING_CHARS, length * 2)
        return raw.decode("utf-16-le", errors="replace")

    def cstring(self, addr: int, max_len: int = 512, encoding: str = "utf-8") -> str:
        raw = self.try_read(addr, max_len) or b""
        return raw.split(b"\0", 1)[0].decode(encoding, errors="replace")

    def __repr__(self) -> str:
        return f"<Process {self.pid} {'rw' if self.can_write else 'ro'}>"

"""Find the local avatar's controller objects in memory, by shape.

Looking a class up by name means finding that name in Mono's metadata heap
and then whatever points at it. That is two wide sweeps, and on this client
it does not always work (see "Finding classes by shape" in the root README). So instead these
locators describe what the object *looks like* and search for that:

    AvControl     eight input axes in [-2, 2] and a run of 0/1 flags
    MotorControl  a motor pointer, a vertical velocity and a move vector

A shape match alone would only be a guess, so every candidate is confirmed
by walking its vtable to the MonoClass and reading the type name back. A
coincidence cannot survive that, which is what makes this safe to write to.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from lcmem.mono import Mono, MonoError
from lcmem.process import Process, ProcessError


class Candidates:
    """Vectorised field access over 8-byte-aligned object starts in a block.

    Candidate k begins at byte ``8 * k``; the helpers read a field at a
    fixed offset inside every candidate at once.
    """

    def __init__(self, data: bytes, span: int):
        self.u8 = np.frombuffer(data, dtype=np.uint8)
        trimmed4 = len(data) - (len(data) % 4)
        trimmed8 = len(data) - (len(data) % 8)
        self.f32 = np.frombuffer(data[:trimmed4], dtype=np.float32)
        self.u64 = np.frombuffer(data[:trimmed8], dtype=np.uint64)
        usable = min(len(self.u8) - span, (len(self.u64) - span // 8) * 8)
        self.k = np.arange(max(usable, 0) // 8, dtype=np.int64)

    def __len__(self) -> int:
        return len(self.k)

    def qword(self, off: int) -> np.ndarray:
        return self.u64[self.k + off // 8]

    def f(self, off: int) -> np.ndarray:
        return self.f32[2 * self.k + off // 4]

    def b(self, off: int) -> np.ndarray:
        return self.u8[8 * self.k + off]


def _pointerish(v: np.ndarray) -> np.ndarray:
    return (v > 0x10000) & (v < 0x7FFFFFFFFFFF) & (v % 8 == 0)


def _small_float(v: np.ndarray, limit: float) -> np.ndarray:
    with np.errstate(invalid="ignore"):
        return np.isfinite(v) & (np.abs(v) <= limit)


@dataclass
class Found:
    address: int
    type_name: str


def _search(mono: Mono, proc: Process, span: int, mask_fn, expect: str) -> list[Found]:
    """Sweep the managed heap for a shape, then confirm the class name."""
    out: list[Found] = []
    seen: set[int] = set()
    for region in mono.cached_heap_regions():
        for base, data in proc.read_chunks(region):
            if len(data) < span + 64:
                continue
            c = Candidates(data, span)
            if not len(c):
                continue
            mask = mask_fn(c)
            for idx in np.flatnonzero(mask):
                addr = base + int(idx) * 8
                if addr in seen:
                    continue
                seen.add(addr)
                try:
                    name = mono.identify(addr)
                except (ProcessError, MonoError):
                    continue
                if name.rsplit(".", 1)[-1] == expect:
                    out.append(Found(addr, name))
    return out


def find_av_control(mono: Mono, proc: Process) -> list[Found]:
    """The local avatar's input controller.

    Recognised by its block of input axes - AxisH/R/U/V and their Last*
    counterparts are all normalised, so eight consecutive floats sit in a
    small range - next to a run of boolean flags.
    """

    def mask(c: Candidates) -> np.ndarray:
        m = _pointerish(c.qword(0x00))
        for off in (0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60):
            m &= _small_float(c.f(off), 2.0)
        for off in (0x40, 0x64, 0x65, 0x66, 0x67, 0x68, 0x94):
            m &= c.b(off) <= 1
        # Smoothing values are real settings, never zero in practice.
        m &= _small_float(c.f(0x38), 1000.0) & _small_float(c.f(0x3C), 1000.0)
        return m

    return _search(mono, proc, 0xB0, mask, "AvControl")


def find_motor_control(mono: Mono, proc: Process) -> list[Found]:
    """The component that turns input into movement.

    Recognised by a live motor pointer followed by a vertical velocity and
    a move vector, all in believable ranges.
    """

    def mask(c: Candidates) -> np.ndarray:
        m = _pointerish(c.qword(0x00))
        m &= _pointerish(c.qword(0xB0))          # CurrentMotor
        m &= _small_float(c.f(0xC4), 1000.0)     # VerticalVel
        for off in (0xC8, 0xCC, 0xD0):           # MoveVector
            m &= _small_float(c.f(off), 1000.0)
        m &= c.b(0xD4) <= 1                      # CharacterFollowsCamera
        return m

    return _search(mono, proc, 0xE0, mask, "MotorControl")

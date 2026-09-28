"""Verify the flight locator and field views against replica structures.

Builds fake AvControl / MotorControl / MotorBase objects - including a
working vtable -> MonoClass -> name chain, so `identify` resolves them just
like the real thing - inside a process we control, then runs the real
locators and views against them.

This covers the parts that would be dangerous to get wrong: that the shape
search finds the right object, that a decoy which merely looks similar is
rejected because its class name differs, that writes land on the intended
fields, and that Restore puts everything back.
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "lc_patches" / "lc_flight"))

from lcmem.mono import ClassLayout, Mono  # noqa: E402
from lcmem.process import Process  # noqa: E402

from flight import AvControlView, MotorControlView, Restore  # noqa: E402
from locate import find_av_control, find_motor_control  # noqa: E402

NAME_OFF, NS_OFF = 0x48, 0x50

TARGET = r'''
import ctypes, struct, time

BUF = ctypes.create_string_buffer(16384)
BASE = ctypes.addressof(BUF)
cursor = [64]

def alloc(size, align=8):
    off = (cursor[0] + align - 1) & ~(align - 1)
    cursor[0] = off + size
    return off

def put(off, fmt, *v):
    struct.pack_into(fmt, BUF, off, *v)

def cstr(text):
    raw = text.encode("ascii") + b"\0"
    off = alloc(len(raw), align=1)
    BUF[off:off + len(raw)] = raw
    return BASE + off

def klass(name, ns):
    """MonoClass with name at +0x48 and namespace at +0x50."""
    off = alloc(0x80)
    put(off + 0x48, "<Q", cstr(name))
    put(off + 0x50, "<Q", cstr(ns))
    return BASE + off

def vtable(name, ns):
    off = alloc(0x20)
    put(off, "<Q", klass(name, ns))
    return BASE + off

AV_VT = vtable("AvControl", "VWW.Clients.Curio.Avatar")
MOTOR_VT = vtable("MotorControl", "VWW.Clients.Curio.Avatar")
BIPED_VT = vtable("MotorBiped", "VWW.Clients.Curio.Avatar")
DECOY_VT = vtable("SomethingElse", "Decoy")

def av_control(vt):
    off = alloc(0xC0)
    put(off, "<Q", vt)
    put(off + 0x38, "<f", 0.5)      # m_TurnSmoothing
    put(off + 0x3C, "<f", 0.25)     # m_StrafeTurnSmoothing
    put(off + 0x40, "<B", 0)        # m_DoubleClickTeleportEnabled
    for i, v in enumerate([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.75, 0.75]):
        put(off + 0x44 + i * 4, "<f", v)   # AxisH..LastAxisV
    for i in range(5):
        put(off + 0x64 + i, "<B", i % 2)   # jump/crouch style flags
    put(off + 0x80, "<B", 0)        # m_InWater
    put(off + 0x94, "<B", 0)        # m_PositionLocked
    return BASE + off

def motor_base():
    off = alloc(0x40)
    put(off, "<Q", BIPED_VT)
    put(off + 0x30, "<f", 4.0)      # UpSpeed
    return BASE + off

def motor_control(vt, motor):
    off = alloc(0xF0)
    put(off, "<Q", vt)
    put(off + 0xB0, "<Q", motor)    # CurrentMotor
    put(off + 0xC4, "<f", -9.81)    # VerticalVel
    for i, v in enumerate([0.0, 0.0, 1.0]):
        put(off + 0xC8 + i * 4, "<f", v)   # MoveVector
    put(off + 0xD4, "<B", 1)
    return BASE + off

av = av_control(AV_VT)
decoy = av_control(DECOY_VT)        # same shape, wrong class name
motor = motor_control(MOTOR_VT, motor_base())
print("ready", av, decoy, motor, flush=True)
while True:
    time.sleep(0.2)
'''

failures = 0


def check(label: str, ok: bool, detail: str = "") -> None:
    global failures
    print(f"[{'ok' if ok else 'FAIL'}] {label}" + (f": {detail}" if detail else ""))
    failures += not ok


def run() -> int:
    child = subprocess.Popen(
        [sys.executable, "-u", "-c", TARGET],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    try:
        parts = child.stdout.readline().split()
        assert parts and parts[0] == "ready", parts
        av_addr, decoy_addr, motor_addr = (int(x) for x in parts[1:4])
        print(f"target pid={child.pid} av@0x{av_addr:X} decoy@0x{decoy_addr:X} "
              f"motor@0x{motor_addr:X}")

        proc = Process.attach(child.pid, write=True)
        check("opened target for writing", proc.can_write)

        mono = Mono(proc)
        mono.layout = ClassLayout(NAME_OFF, NS_OFF)
        # Search everything private+writable rather than relying on the
        # heap-density heuristic, which is tuned for the real game.
        mono._heap_regions = [
            r for r in proc.regions(writable_only=True) if r.type_name == "private"
        ]

        check("identify resolves the fake class chain",
              mono.identify(av_addr) == "VWW.Clients.Curio.Avatar.AvControl",
              mono.identify(av_addr))

        # --- locating -----------------------------------------------------
        found_av = find_av_control(mono, proc)
        addrs = {f.address for f in found_av}
        check("finds AvControl by shape", av_addr in addrs,
              f"{len(found_av)} hit(s)")
        check("rejects the same-shape decoy on class name", decoy_addr not in addrs)

        found_motor = find_motor_control(mono, proc)
        maddrs = {f.address for f in found_motor}
        check("finds MotorControl by shape", motor_addr in maddrs,
              f"{len(found_motor)} hit(s)")

        # --- reading ------------------------------------------------------
        av = AvControlView(proc, av_addr)
        motor = MotorControlView(proc, motor_addr)
        check("reads AxisU", av.axis_u() == 0.0)
        check("reads m_InWater", av.in_water() is False)
        check("reads teleport flag", av.teleport_enabled() is False)
        check("reads VerticalVel", abs(motor.vertical_vel() + 9.81) < 1e-4,
              str(motor.vertical_vel()))
        check("follows CurrentMotor to UpSpeed", motor.up_speed() == 4.0,
              str(motor.up_speed()))

        # --- writing ------------------------------------------------------
        av.set_axis_u(1.0)
        check("writes AxisU", av.axis_u() == 1.0)
        check("keeps LastAxisU in step",
              proc.f32(av_addr + AvControlView.LAST_AXIS_U) == 1.0)
        av.set_in_water(True)
        check("writes m_InWater", av.in_water() is True)
        motor.set_vertical_vel(6.0)
        check("writes VerticalVel", motor.vertical_vel() == 6.0)
        motor.set_up_speed(9.0)
        check("writes UpSpeed through the motor pointer", motor.up_speed() == 9.0)

        # Neighbouring fields must be untouched by all of that.
        check("did not disturb m_TurnSmoothing", proc.f32(av_addr + 0x38) == 0.5)
        check("did not disturb AxisV", proc.f32(av_addr + 0x5C) == 0.75)
        check("did not disturb MoveVector", proc.f32(motor_addr + 0xD0) == 1.0)
        check("decoy left alone", proc.f32(decoy_addr + 0x54) == 0.0)

        # --- restore ------------------------------------------------------
        restore = Restore()
        restore.add("m_InWater", av.set_in_water, False)
        restore.add("VerticalVel", motor.set_vertical_vel, -9.81)
        restore.undo(lambda _m: None)
        check("restore puts m_InWater back", av.in_water() is False)
        check("restore puts VerticalVel back",
              abs(motor.vertical_vel() + 9.81) < 1e-4)

        proc.close()
    finally:
        child.kill()

    print()
    print("FAILURES:", failures)
    return failures


if __name__ == "__main__":
    sys.exit(1 if run() else 0)

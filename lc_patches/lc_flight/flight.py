"""Fly the local avatar, mainly so you can get out of somewhere you're stuck.

This drives the game's *own* movement code rather than fighting it. The
client already has everything needed:

    AvControl.AxisU        an "up" input axis, alongside AxisH / AxisV
    MotorBase.UpSpeed      the speed that axis moves you at
    AvControl.m_InWater    the state in which vertical input is honoured
    MotorControl.VerticalVel   the vertical velocity gravity writes to

Holding the rise key writes AxisU (and, in `swim` mode, tells the client it
is in water so the axis is acted on). Because the movement still goes
through the game's controller, collision and network updates behave normally
- you are moving, not teleporting through geometry.

There is also a one-shot `--unstick`, which is usually all you actually need,
and `--enable-teleport`, which switches on the client's own double-click
teleport (`m_DoubleClickTeleportEnabled`) - the least invasive fix of all,
since it is a feature the game already ships.

    python flight.py --unstick            # one upward nudge, then exit
    python flight.py                      # hold Space / Ctrl to rise / sink
    python flight.py --enable-teleport    # turn on double-click teleport

Every value written is snapshotted first and restored on exit, including on
Ctrl+C. Nothing is injected; this is WriteProcessMemory on values the game
writes itself every frame anyway.
"""
from __future__ import annotations

import argparse
import ctypes
import sys
import time
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from lcmem.mono import Mono, MonoError  # noqa: E402
from lcmem.monometa import AssemblySet  # noqa: E402
from lcmem.process import Process, ProcessError  # noqa: E402

from locate import find_av_control, find_motor_control  # noqa: E402

HERE = Path(__file__).resolve().parent
CACHE_FILE = HERE / ".mono_cache.json"

DEFAULT_MANAGED = Path(
    r"D:\SteamLibrary\steamapps\common\LoveCraft\Application\Curio_Data\Managed"
)
NEEDED = [
    "Assembly-CSharp.dll",
    "Assembly-CSharp-firstpass.dll",
    "VWW.CoreLibs.ClientAPI.dll",
    "VWW.CoreLibs.Network.dll",
]

user32 = ctypes.WinDLL("user32", use_last_error=True)

#: virtual-key codes, see learn.microsoft.com/windows/win32/inputdev
VK = {
    "space": 0x20, "shift": 0x10, "ctrl": 0x11, "alt": 0x12,
    "q": 0x51, "e": 0x45, "x": 0x58, "z": 0x5A, "c": 0x43, "f": 0x46,
    "f7": 0x76, "f8": 0x77, "f9": 0x78, "f10": 0x79,
    "pgup": 0x21, "pgdn": 0x22, "home": 0x24, "end": 0x23,
}


def key_down(vk: int) -> bool:
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)


def parse_key(name: str) -> int:
    key = name.strip().lower()
    if key not in VK:
        raise SystemExit(f"error: unknown key {name!r}; pick from {sorted(VK)}")
    return VK[key]


class AvControlView:
    """The fields of AvControl this tool touches, by verified offset."""

    AXIS_U = 0x54
    LAST_AXIS_U = 0x58
    DOUBLE_CLICK_TELEPORT = 0x40
    JUMP_REQUESTED = 0x64
    IN_WATER = 0x80
    POSITION_LOCKED = 0x94

    def __init__(self, proc: Process, addr: int):
        self.proc = proc
        self.addr = addr

    def axis_u(self) -> float:
        return self.proc.f32(self.addr + self.AXIS_U)

    def set_axis_u(self, value: float) -> None:
        self.proc.set_f32(self.addr + self.AXIS_U, value)
        # The controller compares against the previous frame's value; keeping
        # them equal stops it treating every write as a fresh key press.
        self.proc.set_f32(self.addr + self.LAST_AXIS_U, value)

    def in_water(self) -> bool:
        return bool(self.proc.u8(self.addr + self.IN_WATER))

    def set_in_water(self, on: bool) -> None:
        self.proc.set_u8(self.addr + self.IN_WATER, 1 if on else 0)

    def position_locked(self) -> bool:
        return bool(self.proc.u8(self.addr + self.POSITION_LOCKED))

    def set_position_locked(self, on: bool) -> None:
        self.proc.set_u8(self.addr + self.POSITION_LOCKED, 1 if on else 0)

    def teleport_enabled(self) -> bool:
        return bool(self.proc.u8(self.addr + self.DOUBLE_CLICK_TELEPORT))

    def set_teleport_enabled(self, on: bool) -> None:
        self.proc.set_u8(self.addr + self.DOUBLE_CLICK_TELEPORT, 1 if on else 0)


class MotorControlView:
    """MotorControl's vertical velocity and the motor's up speed."""

    CURRENT_MOTOR = 0xB0
    VERTICAL_VEL = 0xC4
    MOVE_VECTOR = 0xC8
    #: MotorBase
    MOTOR_UP_SPEED = 0x30

    def __init__(self, proc: Process, addr: int):
        self.proc = proc
        self.addr = addr

    def vertical_vel(self) -> float:
        return self.proc.f32(self.addr + self.VERTICAL_VEL)

    def set_vertical_vel(self, value: float) -> None:
        self.proc.set_f32(self.addr + self.VERTICAL_VEL, value)

    def motor(self) -> int:
        return self.proc.ptr(self.addr + self.CURRENT_MOTOR)

    def up_speed(self) -> float | None:
        motor = self.motor()
        if not motor:
            return None
        return self.proc.f32(motor + self.MOTOR_UP_SPEED)

    def set_up_speed(self, value: float) -> None:
        motor = self.motor()
        if motor:
            self.proc.set_f32(motor + self.MOTOR_UP_SPEED, value)


@dataclass
class Restore:
    """Everything written, so it can be put back exactly as it was."""

    entries: list[tuple[str, object]] = field(default_factory=list)

    def add(self, label: str, setter, original) -> None:
        self.entries.append((label, (setter, original)))

    def undo(self, log=print) -> None:
        for label, (setter, original) in reversed(self.entries):
            try:
                setter(original)
            except ProcessError as exc:
                log(f"  could not restore {label}: {exc}")
            else:
                log(f"  restored {label} = {original}")
        self.entries.clear()


def attach(args) -> tuple[Process, Mono, AssemblySet]:
    try:
        proc = Process.attach(args.process, write=True)
    except ProcessError as exc:
        raise SystemExit(f"error: {exc}")
    if not proc.can_write:
        raise SystemExit(
            "error: opened the game read-only, so flight cannot be applied. "
            "Try an elevated terminal."
        )

    managed = Path(args.managed)
    paths = [managed / n for n in NEEDED if (managed / n).exists()]
    asm = AssemblySet(paths) if paths else None

    mono = Mono(proc, asm.assemblies[0] if asm and asm.assemblies else None)
    if args.no_cache or not mono.load_cache(CACHE_FILE):
        print("bootstrapping Mono (one-off, ~1 min)...", file=sys.stderr)
        try:
            mono.bootstrap(args.hint)
        except (MonoError, ProcessError) as exc:
            raise SystemExit(f"error: {exc}")
        mono.save_cache(CACHE_FILE)
    else:
        print("reusing cached Mono layout for this game session", file=sys.stderr)
    return proc, mono, asm


def verify_offsets(asm: AssemblySet) -> None:
    """Confirm the offsets still match the shipped metadata before writing.

    Writing to a wrong offset is far worse than reading one, so this refuses
    to continue if the game has moved a field.
    """
    if asm is None:
        print("warning: metadata not found, offsets unverified", file=sys.stderr)
        return
    checks = [
        ("AvControl", "AxisU", AvControlView.AXIS_U),
        ("AvControl", "LastAxisU", AvControlView.LAST_AXIS_U),
        ("AvControl", "m_InWater", AvControlView.IN_WATER),
        ("AvControl", "m_PositionLocked", AvControlView.POSITION_LOCKED),
        ("AvControl", "m_DoubleClickTeleportEnabled", AvControlView.DOUBLE_CLICK_TELEPORT),
        ("MotorControl", "<VerticalVel>k__BackingField", MotorControlView.VERTICAL_VEL),
        ("MotorControl", "<CurrentMotor>k__BackingField", MotorControlView.CURRENT_MOTOR),
        ("MotorBase", "UpSpeed", MotorControlView.MOTOR_UP_SPEED),
    ]
    problems = []
    for type_name, field_name, expected in checks:
        t = asm.get(type_name)
        if t is None:
            problems.append(f"{type_name} missing from metadata")
            continue
        f = next((f for f in t.instance_fields if f.name == field_name), None)
        if f is None:
            problems.append(f"{type_name}.{field_name} missing")
        elif f.predicted_offset != expected:
            got = "unknown" if f.predicted_offset is None else f"+0x{f.predicted_offset:X}"
            problems.append(
                f"{type_name}.{field_name} moved: expected +0x{expected:X}, "
                f"metadata says {got}"
            )
    if problems:
        raise SystemExit(
            "error: the game's layout no longer matches these offsets, so "
            "writing would corrupt memory:\n  " + "\n  ".join(problems)
        )
    print("offsets verified against shipped metadata", file=sys.stderr)


def locate(mono: Mono, proc: Process, quiet: bool = False):
    if not quiet:
        print("scanning the managed heap for the avatar controller...", file=sys.stderr)
    avs = find_av_control(mono, proc)
    motors = find_motor_control(mono, proc)
    if not quiet:
        print(f"  AvControl   x{len(avs)}   MotorControl x{len(motors)}", file=sys.stderr)
    if not avs:
        raise SystemExit(
            "error: no AvControl found. Log in and be in-world with your "
            "avatar spawned, then try again."
        )
    if len(avs) > 1:
        # AvControl handles local input, so there should only ever be one.
        print(
            f"warning: {len(avs)} AvControl objects; using the first at "
            f"0x{avs[0].address:X}",
            file=sys.stderr,
        )
    return avs[0], (motors[0] if motors else None)


def show_state(av: AvControlView, motor: MotorControlView | None) -> None:
    print(f"AvControl    0x{av.addr:X}")
    print(f"  AxisU                {av.axis_u():.3f}")
    print(f"  m_InWater            {av.in_water()}")
    print(f"  m_PositionLocked     {av.position_locked()}")
    print(f"  double-click teleport {av.teleport_enabled()}")
    if motor:
        print(f"MotorControl 0x{motor.addr:X}")
        print(f"  VerticalVel          {motor.vertical_vel():.3f}")
        up = motor.up_speed()
        print(f"  motor UpSpeed        {up if up is None else f'{up:.3f}'}")


def cmd_status(args) -> int:
    proc, mono, asm = attach(args)
    verify_offsets(asm)
    av_found, motor_found = locate(mono, proc)
    av = AvControlView(proc, av_found.address)
    motor = MotorControlView(proc, motor_found.address) if motor_found else None
    show_state(av, motor)
    return 0


def cmd_enable_teleport(args) -> int:
    """Switch on the client's own double-click teleport."""
    proc, mono, asm = attach(args)
    verify_offsets(asm)
    av_found, _ = locate(mono, proc)
    av = AvControlView(proc, av_found.address)
    before = av.teleport_enabled()
    av.set_teleport_enabled(True)
    after = av.teleport_enabled()
    print(f"double-click teleport: {before} -> {after}")
    if not after:
        print("the game rewrote it immediately; it is probably disabled per-scene")
    else:
        print("double-click the ground to move there. Resets when the game reloads.")
    return 0


def cmd_unstick(args) -> int:
    """One upward nudge - usually enough to clear stuck geometry."""
    proc, mono, asm = attach(args)
    verify_offsets(asm)
    av_found, motor_found = locate(mono, proc)
    av = AvControlView(proc, av_found.address)
    motor = MotorControlView(proc, motor_found.address) if motor_found else None
    restore = Restore()

    was_water = av.in_water()
    was_locked = av.position_locked()
    restore.add("m_InWater", av.set_in_water, was_water)
    restore.add("m_PositionLocked", av.set_position_locked, was_locked)

    print(f"nudging upward for {args.duration:.1f}s...")
    deadline = time.time() + args.duration
    try:
        if was_locked:
            av.set_position_locked(False)
        while time.time() < deadline:
            if args.mode in ("swim", "both"):
                av.set_in_water(True)
                av.set_axis_u(1.0)
            if motor and args.mode in ("velocity", "both"):
                motor.set_vertical_vel(args.speed)
            time.sleep(0.01)
    except KeyboardInterrupt:
        pass
    finally:
        try:
            av.set_axis_u(0.0)
        except ProcessError:
            pass
        print("restoring:")
        restore.undo(lambda m: print(m))
    return 0


def cmd_fly(args) -> int:
    """Hold a key to rise, another to sink."""
    # Resolve the key names before attaching: bootstrapping Mono takes about
    # a minute, and it would be rude to spend it only to reject a typo.
    up_vk, down_vk, toggle_vk = (
        parse_key(args.up), parse_key(args.down), parse_key(args.toggle)
    )
    proc, mono, asm = attach(args)
    verify_offsets(asm)
    av_found, motor_found = locate(mono, proc)
    av = AvControlView(proc, av_found.address)
    motor = MotorControlView(proc, motor_found.address) if motor_found else None
    restore = Restore()
    restore.add("m_InWater", av.set_in_water, av.in_water())
    restore.add("m_PositionLocked", av.set_position_locked, av.position_locked())

    print(f"flight ready on AvControl 0x{av.addr:X}")
    print(f"  {args.up} = up, {args.down} = down, {args.toggle} = toggle, Ctrl+C = quit")
    print(f"  mode {args.mode}, speed {args.speed}")
    enabled = True
    last_toggle = 0.0
    applied = False

    try:
        while True:
            if key_down(toggle_vk) and time.time() - last_toggle > 0.4:
                enabled = not enabled
                last_toggle = time.time()
                print(f"  flight {'on' if enabled else 'off'}")
                if not enabled:
                    _relax(av, motor, args)
                    applied = False

            if not proc.alive:
                print("game exited")
                break

            if enabled:
                rising = key_down(up_vk)
                sinking = key_down(down_vk)
                try:
                    if rising or sinking:
                        direction = 1.0 if rising else -1.0
                        if args.mode in ("swim", "both"):
                            av.set_in_water(True)
                            av.set_axis_u(direction)
                        if motor and args.mode in ("velocity", "both"):
                            motor.set_vertical_vel(direction * args.speed)
                        applied = True
                    elif applied:
                        # Hovering: hold vertical velocity at zero so gravity
                        # does not immediately drag you back down.
                        if args.mode in ("swim", "both"):
                            av.set_axis_u(0.0)
                        if motor and args.mode in ("velocity", "both"):
                            motor.set_vertical_vel(0.0)
                except ProcessError:
                    pass
            time.sleep(args.tick)
    except KeyboardInterrupt:
        print()
    finally:
        try:
            _relax(av, motor, args)
        except ProcessError:
            pass
        print("restoring:")
        restore.undo(lambda m: print(m))
        proc.close()
    return 0


def _relax(av: AvControlView, motor: MotorControlView | None, args) -> None:
    av.set_axis_u(0.0)
    if motor and args.mode in ("velocity", "both"):
        motor.set_vertical_vel(0.0)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="flight",
        description="Fly the local avatar (mainly: get unstuck).",
    )
    p.add_argument("-p", "--process", default="Curio")
    p.add_argument("--managed", default=str(DEFAULT_MANAGED))
    p.add_argument("--hint", default="Lovecraft")
    p.add_argument("--no-cache", action="store_true")
    p.add_argument(
        "-m", "--mode", default="both", choices=["swim", "velocity", "both"],
        help="swim: drive AxisU through the game's water movement; "
             "velocity: write MotorControl.VerticalVel; both (default)",
    )
    p.add_argument("-s", "--speed", type=float, default=6.0,
                   help="vertical speed for velocity mode")
    p.add_argument("--tick", type=float, default=0.01,
                   help="seconds between writes while a key is held")

    sub = p.add_subparsers(dest="command")
    sub.add_parser("status", help="show the current values, write nothing")
    sub.add_parser("enable-teleport", help="turn on the client's double-click teleport")

    un = sub.add_parser("unstick", help="one upward nudge, then restore and exit")
    un.add_argument("-d", "--duration", type=float, default=1.5)

    fly = sub.add_parser("fly", help="hold a key to rise / sink (default)")
    fly.add_argument("--up", default="space")
    fly.add_argument("--down", default="ctrl")
    fly.add_argument("--toggle", default="f8")
    return p


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    command = args.command or "fly"
    if command == "fly":
        for name, default in (("up", "space"), ("down", "ctrl"), ("toggle", "f8")):
            if not hasattr(args, name):
                setattr(args, name, default)
    if command == "unstick" and not hasattr(args, "duration"):
        args.duration = 1.5
    try:
        return {
            "status": cmd_status,
            "enable-teleport": cmd_enable_teleport,
            "unstick": cmd_unstick,
            "fly": cmd_fly,
        }[command](args)
    except (ProcessError, MonoError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

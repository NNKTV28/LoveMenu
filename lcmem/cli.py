"""Command line for lcmem.

Scans are slow (a full sweep reads tens of gigabytes) but refinements are
instant, so results are persisted to a session file between invocations:

    python -m lcmem scan 60 --type f32
    python -m lcmem refine exact 90
    python -m lcmem results

`shell` does the same thing interactively, keeping everything in memory.
"""
from __future__ import annotations

import argparse
import shlex
import sys
import time
from pathlib import Path

import numpy as np

from .monometa import Assembly, MetadataError
from .mono import Mono, MonoError
from .patches import Backups, PatchError, PatchSet, apply_patch, encode, find_aob, revert_patch
from .process import Process, ProcessError, find_pids
from .scanner import TYPES, Scanner

DEFAULT_PROCESS = "Curio"
STATE_DIR = Path(__file__).resolve().parent.parent / ".lcmem"
SESSION_FILE = STATE_DIR / "session.npz"
BACKUP_FILE = STATE_DIR / "backups.json"

#: where the game keeps its managed code, relative to the executable
ASSEMBLY_RELATIVE = Path("Curio_Data/Managed/Assembly-CSharp.dll")


# --- small helpers ---------------------------------------------------------
def human(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if abs(n) < 1024:
            return f"{n:.0f}{unit}"
        n /= 1024
    return f"{n:.1f}TB"


def parse_int(text: str) -> int:
    return int(text, 0)


class Progress:
    """A single rewriting status line; silent when not on a terminal."""

    def __init__(self, label: str, quiet: bool = False):
        self.label = label
        self.quiet = quiet or not sys.stderr.isatty()
        self.start = time.time()
        self.last = 0.0

    def __call__(self, done: int, total: int) -> None:
        if self.quiet:
            return
        now = time.time()
        if done < total and now - self.last < 0.15:
            return
        self.last = now
        pct = 100 * done / total if total else 100
        end = "\n" if done >= total else ""
        print(
            f"\r  {self.label}: {pct:5.1f}%  ({now - self.start:.0f}s)   ",
            end=end, file=sys.stderr, flush=True,
        )


def open_process(args) -> Process:
    target = args.process
    if isinstance(target, str) and target.isdigit():
        target = int(target)
    try:
        return Process.attach(target, write=not args.read_only)
    except ProcessError as exc:
        pids = find_pids(str(args.process))
        extra = f" (candidates: {pids})" if pids else ""
        raise SystemExit(f"error: {exc}{extra}")


def load_assembly(proc: Process | None, args) -> Assembly | None:
    """Find Assembly-CSharp.dll, preferring the running game's own copy."""
    if args.assembly:
        path = Path(args.assembly)
    elif proc is not None:
        try:
            exe = Path(proc.module(f"{args.process}.exe").path)
        except (ProcessError, KeyError):
            exe = None
        path = exe.parent / ASSEMBLY_RELATIVE if exe else None
        if path is None or not path.exists():
            return None
    else:
        return None
    if not path.exists():
        raise SystemExit(f"error: no assembly at {path}")
    try:
        return Assembly(path)
    except MetadataError as exc:
        raise SystemExit(f"error: could not read {path.name}: {exc}")


def save_session(scanner: Scanner) -> None:
    STATE_DIR.mkdir(exist_ok=True)
    np.savez(
        SESSION_FILE,
        addresses=scanner.addresses,
        values=scanner.values,
        type=np.array(scanner.type),
        pid=np.array(scanner.proc.pid),
    )


def load_session(proc: Process, type_override: str | None = None) -> Scanner:
    if not SESSION_FILE.exists():
        raise SystemExit("error: no saved scan - run 'scan' first")
    data = np.load(SESSION_FILE, allow_pickle=False)
    saved_pid = int(data["pid"])
    if saved_pid != proc.pid:
        raise SystemExit(
            f"error: saved scan belongs to PID {saved_pid}, but the game is now "
            f"PID {proc.pid}. Addresses are not comparable; run 'scan' again."
        )
    s = Scanner(proc, type_override or str(data["type"]))
    s.addresses = data["addresses"]
    s.values = data["values"]
    return s


def make_mono(proc: Process, assembly: Assembly | None, args, need_layout: bool = True) -> Mono:
    mono = Mono(proc, assembly)
    if need_layout:
        try:
            mono.bootstrap(args.hint, progress=Progress("locating classes", args.quiet))
        except (MonoError, ProcessError) as exc:
            raise SystemExit(f"error: {exc}")
    return mono


# --- commands --------------------------------------------------------------
def cmd_info(args) -> int:
    proc = open_process(args)
    print(f"process : {proc.pid}  ({'64-bit' if proc.is_64bit else '32-bit'}, "
          f"{'read/write' if proc.can_write else 'READ ONLY'})")
    try:
        print(f"path    : {proc.module(f'{args.process}.exe').path}")
    except ProcessError:
        pass
    regions = list(proc.regions())
    total = sum(r.size for r in regions)
    priv = [r for r in regions if r.type_name == "private" and r.writable]
    print(f"regions : {len(regions)} mapped, {human(total)}")
    print(f"          {len(priv)} private+writable, {human(sum(r.size for r in priv))} (scanned)")
    print("modules :")
    for name in ("Curio.exe", "UnityPlayer.dll", "mono-2.0-bdwgc.dll"):
        try:
            m = proc.module(name)
            print(f"          {m.name:24} 0x{m.base:012X}  {human(m.size)}")
        except ProcessError:
            print(f"          {name:24} not loaded")
    asm = load_assembly(proc, args)
    if asm:
        print(f"metadata: {asm.path.name}, {len(asm.types)} types")
    else:
        print("metadata: not found (pass --assembly to enable name lookups)")
    return 0


def cmd_types(args) -> int:
    asm = load_assembly(open_process(args) if not args.assembly else None, args)
    if asm is None:
        raise SystemExit("error: no assembly found; pass --assembly")
    hits = asm.search(args.pattern) if args.pattern else asm.types
    for t in hits[: args.limit]:
        print(f"{t.full_name:70} {len(t.instance_fields):4} fields  extends {t.extends}")
    print(f"\n{len(hits)} type(s)" + (f", showing {args.limit}" if len(hits) > args.limit else ""))
    return 0


def cmd_type(args) -> int:
    asm = load_assembly(open_process(args) if not args.assembly else None, args)
    if asm is None:
        raise SystemExit("error: no assembly found; pass --assembly")
    t = asm.get(args.name)
    if t is None:
        matches = asm.search(args.name)
        if not matches:
            raise SystemExit(f"error: no type matching {args.name!r}")
        if len(matches) > 1:
            print("did you mean:")
            for m in matches[:15]:
                print("  ", m.full_name)
            return 1
        t = matches[0]
    print(f"{t.full_name}   (extends {t.extends})")
    print(f"own fields start at +0x{t.base_end:X}" if t.base_end is not None
          else "field offsets unknown (base class layout is opaque)")
    print()
    # Mono places reference fields before value fields, so declaration order
    # is not offset order; sort so the layout reads top to bottom.
    ordered = sorted(
        t.fields,
        key=lambda f: (f.predicted_offset is None, f.predicted_offset or 0),
    )
    for f in ordered:
        kind = "static" if f.is_static else ("const" if f.is_const else "")
        off = f"+0x{f.predicted_offset:03X}" if f.predicted_offset is not None else "     "
        scan = f" [{f.scan_type}]" if f.scan_type else ""
        ref = "&" if f.is_reference else " "
        print(f"  {off:>7} {ref} {kind:6} {f.type_name:34} {f.name}{scan}")
    print("\n'&' marks a GC reference; Mono places those before value fields.")
    print("offsets are predicted - confirm with 'object <addr>' before writing.")
    return 0


def cmd_fields(args) -> int:
    asm = load_assembly(open_process(args) if not args.assembly else None, args)
    if asm is None:
        raise SystemExit("error: no assembly found; pass --assembly")
    hits = asm.search_fields(args.pattern)
    if args.scannable:
        hits = [(t, f) for t, f in hits if f.scan_type and f.predicted_offset is not None]
    for t, f in hits[: args.limit]:
        off = f"+0x{f.predicted_offset:03X}" if f.predicted_offset is not None else "     "
        print(f"{t.name:36}.{f.name:32} {f.type_name:16} {off}")
    print(f"\n{len(hits)} field(s)" + (f", showing {args.limit}" if len(hits) > args.limit else ""))
    return 0


def cmd_read(args) -> int:
    proc = open_process(args)
    addr = parse_int(args.address)
    reader = {
        "i8": proc.i8, "u8": proc.u8, "i16": proc.i16, "u16": proc.u16,
        "i32": proc.i32, "u32": proc.u32, "i64": proc.i64, "u64": proc.u64,
        "f32": proc.f32, "f64": proc.f64,
    }[args.type]
    size = TYPES[args.type].itemsize
    for i in range(args.count):
        a = addr + i * size
        try:
            print(f"0x{a:012X}  {reader(a)}")
        except ProcessError as exc:
            print(f"0x{a:012X}  <{exc}>")
    return 0


def cmd_write(args) -> int:
    proc = open_process(args)
    addr = parse_int(args.address)
    payload = encode(args.value, args.type)
    before = proc.read(addr, len(payload))
    proc.write(addr, payload)
    after = proc.read(addr, len(payload))
    print(f"0x{addr:012X}  {before.hex()} -> {after.hex()}")
    if after != payload:
        print("warning: value did not stick; the game may rewrite it every frame")
    return 0


def cmd_dump(args) -> int:
    proc = open_process(args)
    addr = parse_int(args.address)
    data = proc.try_read(addr, args.length)
    if data is None:
        raise SystemExit(f"error: cannot read 0x{addr:X}")
    for off in range(0, len(data), 16):
        row = data[off : off + 16]
        hexs = " ".join(f"{b:02X}" for b in row).ljust(47)
        text = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        print(f"0x{addr + off:012X}  {hexs}  {text}")
    return 0


def cmd_scan(args) -> int:
    proc = open_process(args)
    s = Scanner(
        proc, args.type, align=args.align,
        max_region=None if args.max_region == 0 else args.max_region,
        progress=Progress("scanning", args.quiet),
    )
    if args.mode == "unknown":
        # A dense snapshot is gigabytes and cannot be persisted usefully,
        # so refuse up front rather than after a minute of scanning.
        raise SystemExit(
            "error: 'scan unknown' needs the snapshot to stay in memory.\n"
            "       Use 'python -m lcmem shell', then: scan unknown [budget_mb]"
        )
    t = time.time()
    value2 = parse_value(args.value2, args.type) if args.value2 is not None else None
    n = s.first(args.mode, parse_value(args.value, args.type), value2)
    print(f"{n} hit(s) in {time.time() - t:.0f}s")
    save_session(s)
    show_results(s, args.limit, proc)
    return 0


def parse_value(text, type_name: str):
    if text is None:
        return None
    if type_name in ("f32", "f64"):
        return float(text)
    return int(str(text), 0)


def cmd_refine(args) -> int:
    proc = open_process(args)
    s = load_session(proc)
    s.progress = Progress("refining", args.quiet)
    value = parse_value(args.value, s.type) if args.value is not None else None
    value2 = parse_value(args.value2, s.type) if args.value2 is not None else None
    t = time.time()
    n = s.refine(args.mode, value, value2)
    print(f"{n} hit(s) in {time.time() - t:.1f}s")
    save_session(s)
    show_results(s, args.limit, proc)
    return 0


def show_results(s: Scanner, limit: int, proc: Process) -> None:
    rows = s.results(limit)
    for addr, val in rows:
        print(f"  0x{addr:012X}  {val}   {proc.describe(addr)}")
    if s.count > len(rows):
        print(f"  ... and {s.count - len(rows)} more")


def cmd_results(args) -> int:
    proc = open_process(args)
    s = load_session(proc)
    print(f"{s.count} hit(s), type {s.type}")
    show_results(s, args.limit, proc)
    return 0


def cmd_setall(args) -> int:
    proc = open_process(args)
    s = load_session(proc)
    if s.count > args.max and not args.force:
        raise SystemExit(
            f"error: {s.count} hits is more than --max {args.max}. Narrow the scan "
            "first, or pass --force if you really mean to write them all."
        )
    n = s.write_all(parse_value(args.value, s.type))
    print(f"wrote {args.value} to {n}/{s.count} address(es)")
    return 0


def cmd_strings(args) -> int:
    proc = open_process(args)
    mono = Mono(proc, None)
    hits = mono.find_strings(args.text, limit=args.limit,
                             progress=Progress("searching", args.quiet))
    for o in hits:
        try:
            text = proc.mono_string(o)
        except ProcessError:
            text = "<unreadable>"
        print(f"  0x{o:012X}  {text[:100]!r}")
    print(f"\n{len(hits)} managed string(s)")
    return 0


def cmd_identify(args) -> int:
    proc = open_process(args)
    asm = load_assembly(proc, args)
    mono = make_mono(proc, asm, args)
    addr = parse_int(args.address)
    name = mono.identify(addr)
    if name:
        print(f"0x{addr:012X} is a {name}")
        return 0
    found = mono.identify_containing(addr, back=args.back)
    if found:
        obj, name = found
        print(f"0x{addr:012X} is inside a {name} at 0x{obj:012X} (+0x{addr - obj:X})")
        return 0
    print(f"0x{addr:012X} is not a managed object (searched back {args.back} bytes)")
    return 1


def cmd_class(args) -> int:
    proc = open_process(args)
    asm = load_assembly(proc, args)
    mono = make_mono(proc, asm, args)
    hits = mono.find_class(args.name, args.namespace,
                           progress=Progress("searching", args.quiet))
    for k in hits:
        ns, nm = mono.class_name(k)
        print(f"  0x{k:012X}  {ns + '.' if ns else ''}{nm}")
    if not hits:
        print("no MonoClass found - is the type loaded yet?")
    return 0 if hits else 1


def cmd_instances(args) -> int:
    proc = open_process(args)
    asm = load_assembly(proc, args)
    mono = make_mono(proc, asm, args)
    classes = mono.find_class(args.name, args.namespace,
                              progress=Progress("locating class", args.quiet))
    if not classes:
        raise SystemExit(f"error: class {args.name!r} not found in the process")
    if len(classes) > 1 and args.namespace is None:
        print(f"note: {len(classes)} classes named {args.name}; using the first. "
              "Pass --namespace to disambiguate.")
    inst = mono.find_instances(classes[0], progress=Progress("finding instances", args.quiet))
    for a in inst[: args.limit]:
        print(f"  0x{int(a):012X}  {mono.identify(int(a))}")
    print(f"\n{len(inst)} instance(s)")
    return 0


def cmd_object(args) -> int:
    proc = open_process(args)
    asm = load_assembly(proc, args)
    if asm is None:
        raise SystemExit("error: no assembly found; pass --assembly")
    mono = make_mono(proc, asm, args, need_layout=True)
    addr = parse_int(args.address)
    type_name = args.type_name or mono.identify(addr).split(".")[-1]
    if not type_name:
        raise SystemExit(f"error: 0x{addr:X} is not a managed object; pass --as to force a type")
    info = asm.get(type_name)
    if info is None:
        raise SystemExit(f"error: no metadata for type {type_name!r}")
    print(f"0x{addr:012X} as {info.full_name}")
    for name, tname, off, val in mono.read_fields(addr, info):
        label = f"+0x{off:03X}" if off is not None else "  -  "
        print(f"  {label:>7} {tname:28} {name:30} = {val}")
    return 0


def cmd_aob(args) -> int:
    proc = open_process(args)
    hits = find_aob(proc, args.pattern, not args.all_memory, args.limit,
                    progress=Progress("searching", args.quiet))
    for h in hits:
        print(f"  0x{h:012X}  {proc.describe(h)}")
    print(f"\n{len(hits)} match(es)")
    return 0


def cmd_watch(args) -> int:
    proc = open_process(args)
    addr = parse_int(args.address)
    reader = {
        "i8": proc.i8, "u8": proc.u8, "i16": proc.i16, "u16": proc.u16,
        "i32": proc.i32, "u32": proc.u32, "i64": proc.i64, "u64": proc.u64,
        "f32": proc.f32, "f64": proc.f64,
    }[args.type]
    print(f"watching 0x{addr:X} as {args.type} - Ctrl+C to stop")
    last = object()
    try:
        while True:
            try:
                cur = reader(addr)
            except ProcessError as exc:
                print(f"  <{exc}>")
                break
            if cur != last:
                print(f"  {time.strftime('%H:%M:%S')}  {cur}")
                last = cur
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print()
    return 0


def cmd_patch(args) -> int:
    proc = open_process(args)
    STATE_DIR.mkdir(exist_ok=True)
    backups = Backups(BACKUP_FILE, proc)

    if args.action == "list":
        ps = PatchSet.load(args.file)
        for p in ps:
            state = "APPLIED" if p.name in backups else ("on" if p.enabled else "off")
            print(f"  [{state:7}] {p.name:28} {p.description}")
        return 0

    if args.action == "revert":
        names = [args.name] if args.name else list(backups.data)
        if not names:
            print("nothing to revert")
            return 0
        for name in names:
            try:
                result = revert_patch(proc, name, backups)
            except PatchError as exc:
                print(f"  {name}: {exc}")
                continue
            if result:
                addr, original = result
                print(f"  reverted {name} at 0x{addr:X} ({original.hex()})")
        return 0

    # apply
    ps = PatchSet.load(args.file)
    chosen = [ps.get(args.name)] if args.name else [p for p in ps if p.enabled]
    if not chosen:
        print("no enabled patches")
        return 0
    asm = load_assembly(proc, args)
    needs_mono = any(p.kind == "mono" for p in chosen)
    mono = make_mono(proc, asm, args) if needs_mono else None
    failures = 0
    for p in chosen:
        try:
            addr, original, written = apply_patch(proc, p, backups, mono)
            print(f"  {p.name:28} 0x{addr:012X}  {original.hex()} -> {written.hex()}")
        except (PatchError, ProcessError) as exc:
            print(f"  {p.name:28} FAILED: {exc}")
            failures += 1
    return 1 if failures else 0


def cmd_shell(args) -> int:
    """Interactive session; keeps scan results and Mono state in memory."""
    proc = open_process(args)
    asm = load_assembly(proc, args)
    mono = Mono(proc, asm)
    scanner = Scanner(proc, args.type, progress=Progress("scanning", args.quiet))
    print(f"lcmem shell - PID {proc.pid}, type {scanner.type}. 'help' for commands.")

    def ensure_mono() -> bool:
        if mono.layout is None:
            try:
                mono.bootstrap(args.hint, progress=Progress("locating classes", args.quiet))
            except (MonoError, ProcessError) as exc:
                print(f"error: {exc}")
                return False
        return True

    while True:
        try:
            line = input("lcmem> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break
        if not line:
            continue
        try:
            parts = shlex.split(line)
        except ValueError as exc:
            print(f"error: {exc}")
            continue
        cmd, rest = parts[0], parts[1:]
        try:
            if cmd in ("quit", "exit"):
                break
            elif cmd == "help":
                print(SHELL_HELP)
            elif cmd == "type":
                scanner = Scanner(proc, rest[0], progress=scanner.progress)
                print(f"scan type is now {scanner.type}")
            elif cmd == "scan":
                mode = rest[0] if rest and rest[0] in ("unknown",) else "exact"
                t = time.time()
                if mode == "unknown":
                    n = scanner.first_unknown(budget_mb=int(rest[1]) if len(rest) > 1 else 1024)
                else:
                    n = scanner.first("exact", parse_value(rest[0], scanner.type))
                print(f"{n} hit(s) in {time.time() - t:.0f}s")
            elif cmd == "refine":
                mode = rest[0]
                v = parse_value(rest[1], scanner.type) if len(rest) > 1 else None
                v2 = parse_value(rest[2], scanner.type) if len(rest) > 2 else None
                t = time.time()
                print(f"{scanner.refine(mode, v, v2)} hit(s) in {time.time() - t:.1f}s")
            elif cmd == "results":
                for a, v in scanner.results(int(rest[0]) if rest else 20):
                    print(f"  0x{a:012X}  {v}   {proc.describe(a)}")
            elif cmd == "setall":
                print(f"wrote to {scanner.write_all(parse_value(rest[0], scanner.type))} address(es)")
            elif cmd == "read":
                a = parse_int(rest[0])
                print(scanner.read_values(np.array([a], dtype=np.uint64))[0][0])
            elif cmd == "write":
                a = parse_int(rest[0])
                proc.write(a, encode(rest[1], scanner.type))
                print("ok")
            elif cmd == "dump":
                data = proc.read(parse_int(rest[0]), int(rest[1]) if len(rest) > 1 else 128)
                for off in range(0, len(data), 16):
                    row = data[off : off + 16]
                    hexs = " ".join(f"{b:02X}" for b in row).ljust(47)
                    text = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
                    print(f"0x{parse_int(rest[0]) + off:012X}  {hexs}  {text}")
            elif cmd == "identify":
                if ensure_mono():
                    a = parse_int(rest[0])
                    found = mono.identify(a) or mono.identify_containing(a)
                    print(found or "not a managed object")
            elif cmd == "object":
                if ensure_mono() and asm:
                    a = parse_int(rest[0])
                    tn = rest[1] if len(rest) > 1 else mono.identify(a).split(".")[-1]
                    info = asm.get(tn)
                    if info is None:
                        print(f"no metadata for {tn!r}")
                    else:
                        for name, tname, off, val in mono.read_fields(a, info):
                            lbl = f"+0x{off:03X}" if off is not None else "  -  "
                            print(f"  {lbl:>7} {tname:26} {name:28} = {val}")
            elif cmd == "strings":
                for o in mono.find_strings(rest[0], limit=int(rest[1]) if len(rest) > 1 else 20):
                    print(f"  0x{o:012X}  {proc.mono_string(o)[:80]!r}")
            else:
                print(f"unknown command {cmd!r}; try 'help'")
        except (IndexError, ValueError) as exc:
            print(f"error: bad arguments ({exc})")
        except (ProcessError, MonoError) as exc:
            print(f"error: {exc}")
    return 0


SHELL_HELP = """
  type <t>                 set scan type (i32 u32 f32 f64 i64 u8 ...)
  scan <value>             first scan for a value
  scan unknown [budget_mb] snapshot memory without knowing the value
  refine <mode> [v] [v2]   exact not greater less between changed unchanged
                           increased decreased increased_by decreased_by
  results [n]              show current hits
  setall <value>           write a value to every hit
  read <addr>              read one value
  write <addr> <value>     write one value
  dump <addr> [len]        hexdump
  identify <addr>          what managed object is this
  object <addr> [Type]     read an object's fields through metadata
  strings <text> [n]       find managed strings
  quit
"""


# --- argument wiring -------------------------------------------------------
def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="lcmem",
        description="Read and modify the memory of the Lovecraft (Curio) client.",
    )
    p.add_argument("-p", "--process", default=DEFAULT_PROCESS,
                   help="process name or PID (default: Curio)")
    p.add_argument("--assembly", help="path to Assembly-CSharp.dll (auto-detected)")
    p.add_argument("--read-only", action="store_true", help="never open the process for writing")
    p.add_argument("--hint", default="Lovecraft",
                   help="a string present in game memory, used to bootstrap Mono lookups")
    p.add_argument("-q", "--quiet", action="store_true", help="no progress output")
    sub = p.add_subparsers(dest="command", required=True)

    def add(name, fn, help_text):
        sp = sub.add_parser(name, help=help_text)
        sp.set_defaults(func=fn)
        return sp

    add("info", cmd_info, "show process, modules and memory layout")

    sp = add("types", cmd_types, "list types in the game assembly")
    sp.add_argument("pattern", nargs="?", help="substring to match")
    sp.add_argument("--limit", type=int, default=60)

    sp = add("type", cmd_type, "show one type's fields and predicted offsets")
    sp.add_argument("name")

    sp = add("fields", cmd_fields, "search fields across all types")
    sp.add_argument("pattern")
    sp.add_argument("--limit", type=int, default=60)
    sp.add_argument("--scannable", action="store_true",
                    help="only fields with a known offset and primitive type")

    sp = add("read", cmd_read, "read a value")
    sp.add_argument("address")
    sp.add_argument("-t", "--type", default="i32", choices=sorted(TYPES))
    sp.add_argument("-n", "--count", type=int, default=1)

    sp = add("write", cmd_write, "write a value")
    sp.add_argument("address")
    sp.add_argument("value")
    sp.add_argument("-t", "--type", default="i32")

    sp = add("dump", cmd_dump, "hexdump memory")
    sp.add_argument("address")
    sp.add_argument("-n", "--length", type=int, default=128)

    sp = add("scan", cmd_scan, "first scan for a value")
    sp.add_argument("value")
    sp.add_argument("value2", nargs="?")
    sp.add_argument("-t", "--type", default="i32", choices=sorted(TYPES))
    sp.add_argument("-m", "--mode", default="exact",
                    choices=["exact", "not", "greater", "less", "between", "unknown"])
    sp.add_argument("--align", type=int, default=None)
    sp.add_argument("--max-region", type=int, default=256 * 1024 * 1024,
                    help="skip regions bigger than this (0 = no limit)")
    sp.add_argument("--budget", type=int, default=1024, help="MB to snapshot for unknown scans")
    sp.add_argument("--limit", type=int, default=20)

    sp = add("refine", cmd_refine, "narrow the previous scan")
    sp.add_argument("mode")
    sp.add_argument("value", nargs="?")
    sp.add_argument("value2", nargs="?")
    sp.add_argument("--limit", type=int, default=20)

    sp = add("results", cmd_results, "show the current scan results")
    sp.add_argument("--limit", type=int, default=40)

    sp = add("setall", cmd_setall, "write a value to every current hit")
    sp.add_argument("value")
    sp.add_argument("--max", type=int, default=64, help="refuse to write more hits than this")
    sp.add_argument("--force", action="store_true")

    sp = add("strings", cmd_strings, "find managed strings by content")
    sp.add_argument("text")
    sp.add_argument("--limit", type=int, default=40)

    sp = add("identify", cmd_identify, "identify the object at an address")
    sp.add_argument("address")
    sp.add_argument("--back", type=int, default=0x400)

    sp = add("class", cmd_class, "find a MonoClass by name")
    sp.add_argument("name")
    sp.add_argument("--namespace")

    sp = add("instances", cmd_instances, "find every live instance of a class")
    sp.add_argument("name")
    sp.add_argument("--namespace")
    sp.add_argument("--limit", type=int, default=40)

    sp = add("object", cmd_object, "read an object's fields through metadata")
    sp.add_argument("address")
    sp.add_argument("--as", dest="type_name", help="force a type name")

    sp = add("aob", cmd_aob, "search for a byte signature (?? wildcards)")
    sp.add_argument("pattern")
    sp.add_argument("--limit", type=int, default=32)
    sp.add_argument("--all-memory", action="store_true",
                    help="search data too, not just executable pages")

    sp = add("watch", cmd_watch, "poll an address and print changes")
    sp.add_argument("address")
    sp.add_argument("-t", "--type", default="i32", choices=sorted(TYPES))
    sp.add_argument("-i", "--interval", type=float, default=0.25)

    sp = add("patch", cmd_patch, "apply or revert declarative patches")
    sp.add_argument("action", choices=["list", "apply", "revert"])
    sp.add_argument("name", nargs="?", help="a single patch; default is all")
    sp.add_argument("-f", "--file", default=str(Path("patches") / "community.json"))

    sp = add("shell", cmd_shell, "interactive session (keeps results in memory)")
    sp.add_argument("-t", "--type", default="i32", choices=sorted(TYPES))

    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        return args.func(args)
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)
        return 130
    except (ProcessError, MonoError, PatchError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())

"""End-to-end check against a throwaway target process we control.

Spawns a child holding known values, then scans, refines and writes them
from the outside - the same path used against the game, but verifiable.
"""
from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from lcmem.process import Process  # noqa: E402
from lcmem.scanner import Scanner  # noqa: E402

TARGET = r"""
import ctypes, sys, time
i = ctypes.c_int32(123456789)
f = ctypes.c_float(1234.5)
print("ready", ctypes.addressof(i), ctypes.addressof(f), flush=True)
last = (i.value, f.value)
while True:
    cur = (i.value, f.value)
    if cur != last:
        print("changed", cur[0], cur[1], flush=True)
        last = cur
    time.sleep(0.05)
"""

MAGIC_I = 123456789
MAGIC_F = 1234.5


def run() -> int:
    child = subprocess.Popen(
        [sys.executable, "-u", "-c", TARGET],
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
    )
    failures = 0
    try:
        line = child.stdout.readline().split()
        assert line[0] == "ready", line
        addr_i, addr_f = int(line[1]), int(line[2])
        print(f"target pid={child.pid} int@0x{addr_i:X} float@0x{addr_f:X}")

        proc = Process.attach(child.pid)

        # --- typed reads hit the right values ---------------------------
        got_i, got_f = proc.i32(addr_i), proc.f32(addr_f)
        ok = got_i == MAGIC_I and abs(got_f - MAGIC_F) < 1e-3
        print(f"[{'ok' if ok else 'FAIL'}] direct read: {got_i}, {got_f}")
        failures += not ok

        # --- int32 scan finds the known address -------------------------
        s = Scanner(proc, "i32")
        n = s.first("exact", MAGIC_I)
        found = addr_i in set(int(a) for a in s.addresses)
        print(f"[{'ok' if found else 'FAIL'}] i32 scan: {n} hit(s), target present={found}")
        failures += not found

        # --- float scan, including tolerance ----------------------------
        sf = Scanner(proc, "f32")
        nf = sf.first("exact", MAGIC_F)
        found_f = addr_f in set(int(a) for a in sf.addresses)
        print(f"[{'ok' if found_f else 'FAIL'}] f32 scan: {nf} hit(s), target present={found_f}")
        failures += not found_f

        # --- write, and confirm the child observes it -------------------
        proc.set_i32(addr_i, 555)
        proc.set_f32(addr_f, 9.25)
        deadline = time.time() + 3
        seen = None
        while time.time() < deadline:
            line = child.stdout.readline()
            if line.startswith("changed"):
                parts = line.split()
                seen = (int(parts[1]), float(parts[2]))
                break
        ok = seen == (555, 9.25)
        print(f"[{'ok' if ok else 'FAIL'}] write observed by target: {seen}")
        failures += not ok

        # --- refine narrows to the value we just wrote ------------------
        s.refine("exact", 555)
        ok = addr_i in set(int(a) for a in s.addresses)
        print(f"[{'ok' if ok else 'FAIL'}] refine after write: {s.count} hit(s), present={ok}")
        failures += not ok

        # --- unaligned scan still locates the value ---------------------
        su = Scanner(proc, "i32", align=1)
        su.first("exact", 555)
        ok = addr_i in set(int(a) for a in su.addresses)
        print(f"[{'ok' if ok else 'FAIL'}] align=1 scan: {su.count} hit(s), present={ok}")
        failures += not ok

        # --- unknown-value snapshot then 'changed' ----------------------
        s2 = Scanner(proc, "i32")
        s2.first_unknown(budget_mb=64)
        snap_n = s2.count
        proc.set_i32(addr_i, 777)
        time.sleep(0.2)
        s2.refine("changed")
        ok = addr_i in set(int(a) for a in s2.addresses)
        print(
            f"[{'ok' if ok else 'FAIL'}] unknown->changed: {snap_n} slots -> "
            f"{s2.count} hit(s), present={ok}"
        )
        failures += not ok

        # --- read_values batching matches direct reads ------------------
        import numpy as np
        addrs = np.array([addr_i, addr_f, addr_i + 4], dtype=np.uint64)
        vals, okm = Scanner(proc, "i32").read_values(addrs)
        ok = okm.all() and int(vals[0]) == 777
        print(f"[{'ok' if ok else 'FAIL'}] batched read_values: {vals.tolist()}")
        failures += not ok

        proc.close()
    finally:
        child.kill()

    print()
    print("FAILURES:", failures)
    return failures


if __name__ == "__main__":
    sys.exit(1 if run() else 0)

"""Verify the chat reader against byte-exact replicas of the game's structures.

The client only creates its chat classes once chat actually runs, so we
cannot rely on the game being in the right state to test the parser. Instead
this builds ChatChannelInfo / ChatChannelMessageEventArgs / ChannelMember /
List<T> / System.String objects at the documented offsets inside a process we
control, and reads them back with the real ChatReader.

That exercises everything the logger does with a live game - offsets, list
walking, DateTime decoding, UTF-16 strings, folder routing and rendering -
without needing anyone to be logged in.
"""
from __future__ import annotations

import subprocess
import sys
import tempfile
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
sys.path.insert(0, str(ROOT / "lc_patches" / "chat_integration"))

from lcmem.mono import Mono  # noqa: E402
from lcmem.process import Process  # noqa: E402

import chat_logger  # noqa: E402
from chat_logger import ChatReader, Writer  # noqa: E402

# The child builds the object graph and prints the two channel addresses.
TARGET = r'''
import ctypes, struct, sys, time
from datetime import datetime

BUF = ctypes.create_string_buffer(8192)
BASE = ctypes.addressof(BUF)
cursor = [64]

def alloc(size, align=8):
    off = (cursor[0] + align - 1) & ~(align - 1)
    cursor[0] = off + size
    return off

def put(off, fmt, *vals):
    struct.pack_into(fmt, BUF, off, *vals)

def mono_string(text):
    """vtable, sync, length at +0x10, UTF-16 chars at +0x14."""
    raw = text.encode("utf-16-le")
    off = alloc(0x14 + len(raw) + 2)
    put(off, "<Q", 0xDEADBEEF00)          # vtable placeholder
    put(off + 0x10, "<i", len(text))
    BUF[off + 0x14 : off + 0x14 + len(raw)] = raw
    return BASE + off

def ticks(dt):
    delta = dt - datetime(1, 1, 1)
    return int(delta.total_seconds()) * 10_000_000 + delta.microseconds * 10

def member(name):
    off = alloc(0x20)
    put(off, "<Q", 0xDEADBEEF00)
    put(off + 0x10, "<Q", mono_string(name))   # <Name>
    return BASE + off

def message(text, sender, when, style):
    off = alloc(0x58)
    put(off, "<Q", 0xDEADBEEF00)
    put(off + 0x28, "<Q", member(sender) if sender else 0)   # <Participant>
    put(off + 0x30, "<Q", mono_string(text))                 # <Message>
    put(off + 0x40, "<Q", ticks(when))                       # <TimeStamp>
    put(off + 0x48, "<i", style)                             # <Style>
    return BASE + off

def make_list(items):
    """MonoArray: max_length at +0x18, data at +0x20.
       List<T>: _items +0x10, _syncRoot +0x18, _size +0x20."""
    capacity = len(items) + 3          # deliberately longer than _size
    arr = alloc(0x20 + capacity * 8)
    put(arr, "<Q", 0xDEADBEEF00)
    put(arr + 0x18, "<q", capacity)
    for i, it in enumerate(items):
        put(arr + 0x20 + i * 8, "<Q", it)
    # Junk past _size must be ignored by the reader.
    for i in range(len(items), capacity):
        put(arr + 0x20 + i * 8, "<Q", 0x4141414141)
    lst = alloc(0x28)
    put(lst, "<Q", 0xDEADBEEF00)
    put(lst + 0x10, "<Q", BASE + arr)
    put(lst + 0x20, "<i", len(items))
    return BASE + lst

def channel(name, display, messages, local=0, private=0, system=0, peer=None):
    off = alloc(0x70)
    put(off, "<Q", 0xDEADBEEF00)
    put(off + 0x10, "<Q", mono_string(name))
    put(off + 0x18, "<Q", mono_string(display))
    put(off + 0x28, "<Q", member(peer) if peer else 0)
    put(off + 0x50, "<Q", make_list(messages))
    put(off + 0x69, "<B", local)
    put(off + 0x6A, "<B", private)
    put(off + 0x6B, "<B", system)
    return BASE + off

when = datetime(2026, 9, 9, 21, 2)
local_ch = channel(
    "local", "Local",
    [
        message("привет? есть кто?",
                "some_player", when, 0),
        message("You entered new location", None, when, 3),
        message("<color=#ff0000>red</color> text", "someone", when, 0),
    ],
    local=1,
)
dm = channel(
    "dm-guid", "Direct",
    [message("hey there", "some_player", when, 1)],
    private=1, peer="some_player",
)
print("ready", local_ch, dm, flush=True)
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
        line = child.stdout.readline().split()
        assert line and line[0] == "ready", line
        local_addr, dm_addr = int(line[1]), int(line[2])
        print(f"target pid={child.pid} local@0x{local_addr:X} dm@0x{dm_addr:X}")

        proc = Process.attach(child.pid, write=False)
        mono = Mono(proc)          # no bootstrap needed: no class lookups here
        reader = ChatReader(proc, mono, verbose=False)

        # --- channel metadata -------------------------------------------
        local = reader._read_channel(local_addr)
        check("local channel parses", local is not None)
        if local:
            check("local kind", local.kind == "local", local.kind)
            check("local label", local.label == "Local", local.label)

        dm = reader._read_channel(dm_addr)
        check("dm channel parses", dm is not None)
        if dm:
            check("dm kind", dm.kind == "private", dm.kind)
            check("dm filed under the other person", dm.label == "some_player", dm.label)

        # --- messages ----------------------------------------------------
        msgs = reader.messages(local)
        check("reads exactly _size messages, ignoring array slack",
              len(msgs) == 3, str(len(msgs)))
        if len(msgs) == 3:
            first = msgs[0]
            check("sender", first.sender == "some_player", str(first.sender))
            check("cyrillic text survives",
                  first.text == "привет? есть кто?", repr(first.text))
            check("timestamp decoded",
                  first.when == datetime(2026, 9, 9, 21, 2), str(first.when))
            # This is the line from the screenshot.
            check("renders like the client",
                  first.render() == "21:02 [some_player] : привет? есть кто?",
                  repr(first.render()))
            check("notification has no sender and no brackets",
                  msgs[1].sender is None
                  and msgs[1].render() == "21:02 You entered new location",
                  repr(msgs[1].render()))
            check("rich text stripped",
                  msgs[2].render() == "21:02 [someone] : red text",
                  repr(msgs[2].render()))

        # --- files -------------------------------------------------------
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            chat_logger.STATE_FILE = root / ".state.json"
            writer = Writer(root, jsonl=True)
            n1 = writer.write(local, msgs)
            n2 = writer.write(dm, reader.messages(dm))
            check("wrote every message once", n1 == 3 and n2 == 1, f"{n1},{n2}")

            day = datetime(2026, 9, 9).strftime("%Y-%m-%d")
            local_file = root / "local" / f"{day}.log"
            # Only local is a flat stream; everything else gets a folder.
            dm_file = root / "private" / "some_player" / f"{day}.log"
            check("local/<date>.log exists", local_file.exists(), str(local_file))
            check("private/<person>/<date>.log exists", dm_file.exists(), str(dm_file))

            if local_file.exists():
                lines = local_file.read_text(encoding="utf-8").splitlines()
                check("file matches the screenshot format",
                      lines[0] == "21:02 [some_player] : привет? есть кто?",
                      repr(lines[0]))
            if dm_file.exists():
                check("dm line written",
                      dm_file.read_text(encoding="utf-8").strip()
                      == "21:02 [some_player] : hey there")

            # Re-writing the same messages must not duplicate them.
            again = writer.write(local, msgs)
            check("dedup on second pass", again == 0, str(again))
            if local_file.exists():
                check("file still has 3 lines",
                      len(local_file.read_text(encoding="utf-8").splitlines()) == 3)

            jsonl = root / "local" / f"{day}.jsonl"
            check("jsonl written", jsonl.exists())
            if jsonl.exists():
                import json
                rows = [json.loads(x) for x in
                        jsonl.read_text(encoding="utf-8").splitlines()]
                check("jsonl keeps raw markup",
                      rows[2]["text"] == "<color=#ff0000>red</color> text",
                      rows[2]["text"])
                check("jsonl labels the style",
                      rows[1]["style"] == "Notification", rows[1]["style"])

        proc.close()
    finally:
        child.kill()

    print()
    print("FAILURES:", failures)
    return failures


if __name__ == "__main__":
    sys.exit(1 if run() else 0)

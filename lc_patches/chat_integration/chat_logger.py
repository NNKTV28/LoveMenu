"""Read the Lovecraft client's chat out of memory and write it to disk.

Chat lives in memory as `ChatChannelMessageEventArgs` objects, each pointing
at the `ChatChannelInfo` for the conversation it belongs to. This collects
them and appends new lines to per-conversation log files:

    chat_integration/
      local/2026-09-09.log
      private/SomePlayer/2026-09-09.log
      channel/SomeGroup/2026-09-09.log
      system/GLOBAL EUROPE/2026-09-09.log

Lines are written the way the client renders them:

    21:02 [some_player] : привет? есть кто?

Nothing is written into the game - this only reads.

Messages are found by *shape*, not by class name. Looking a class up by name
means locating that name in Mono's metadata heap and then whatever points at
it, which is two wide sweeps and - as it turned out on this client - can
simply fail. But we know exactly what a chat message looks like: a string
pointer at +0x30, believable DateTime ticks at +0x40, and a 0..3 enum at
+0x48. All messages share one vtable, so the shape finds it in a single heap
sweep, and the class name is then read back through that vtable to confirm
it really is ChatChannelMessageEventArgs.

The channels come along for free, since every message points at its own.
Note the client leaves `ChatChannelInfo.m_Messages` empty in practice, so
the standalone message objects are the real source of truth.

Usage:
    python chat_logger.py                 # follow chat, writing as it arrives
    python chat_logger.py --once          # dump what is in memory and exit
    python chat_logger.py --interval 5    # poll every 5 seconds
"""
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

import numpy as np  # noqa: E402

from lcmem.mono import Mono, MonoError  # noqa: E402
from lcmem.monometa import AssemblySet  # noqa: E402
from lcmem.process import Process, ProcessError  # noqa: E402

HERE = Path(__file__).resolve().parent
STATE_FILE = HERE / ".chat_state.json"
CACHE_FILE = HERE / ".mono_cache.json"

DEFAULT_MANAGED = Path(
    r"D:\SteamLibrary\steamapps\common\LoveCraft\Application\Curio_Data\Managed"
)

#: only these assemblies are needed to resolve the chat types
NEEDED = [
    "Assembly-CSharp.dll",
    "VWW.CoreLibs.ClientAPI.dll",
    "VWW.CoreLibs.Network.dll",
    "VWW.CoreLibs.Shared.dll",
]

CHAT_CHANNEL_INFO = "ChatChannelInfo"
MESSAGE_TYPE = "ChatChannelMessageEventArgs"
CHAT_CHANNEL_NS = "VWW.CoreLibs.Network.Chat"

#: ChatStyle members, in declaration order. The metadata parser does not
#: read the Constant table, so these are assumed to be the default 0..3 -
#: an unlabelled style shows as its number. Only the .jsonl "style" label
#: depends on this; which folder a message lands in comes from the channel's
#: own IsLocal / IsPrivate / IsSystem flags, which are read directly.
STYLE_NAMES = {0: "Local", 1: "Private", 2: "Channel", 3: "Notification"}

RICH_TEXT = re.compile(r"</?(?:color|size|b|i|material|quad|link|style)\b[^>]*>", re.I)
UNSAFE = re.compile(r'[<>:"/\\|?*\x00-\x1f]')


def _ticks(dt: datetime) -> int:
    """.NET DateTime ticks: 100ns units since 0001-01-01."""
    delta = dt - datetime(1, 1, 1)
    return int(delta.total_seconds()) * 10_000_000 + delta.microseconds * 10


def clean_name(name: str, fallback: str = "unknown") -> str:
    """Make a channel or person name safe to use as a folder name."""
    name = RICH_TEXT.sub("", name or "").strip()
    name = UNSAFE.sub("_", name).strip(". ")
    return name[:64] or fallback


def strip_rich_text(text: str) -> str:
    return RICH_TEXT.sub("", text or "")


@dataclass
class Message:
    when: datetime | None
    sender: str | None
    text: str
    style: int
    channel: str

    @property
    def key(self) -> str:
        stamp = self.when.isoformat() if self.when else "?"
        return f"{self.channel}|{stamp}|{self.sender or ''}|{self.text}"

    def render(self) -> str:
        clock = self.when.strftime("%H:%M") if self.when else "--:--"
        body = strip_rich_text(self.text).strip()
        if self.sender:
            return f"{clock} [{self.sender}] : {body}"
        return f"{clock} {body}"


@dataclass
class Channel:
    address: int
    name: str
    display: str
    is_local: bool
    is_private: bool
    is_system: bool
    peer: str | None

    @property
    def kind(self) -> str:
        if self.is_local:
            return "local"
        if self.is_private:
            return "private"
        if self.is_system:
            return "system"
        return "channel"

    @property
    def label(self) -> str:
        """The name this conversation is filed under."""
        if self.is_private:
            return clean_name(self.peer or self.display or self.name, "private")
        return clean_name(self.display or self.name, self.kind)

    def path(self, root: Path, when: datetime) -> Path:
        """local/<date>.log, private/<person>/<date>.log, channel/<name>/...

        Only local chat is a single stream. The global channels all carry
        IsSystem but are genuinely separate conversations (GLOBAL EUROPE,
        GLOBAL ASIA, English...), so they get a folder each rather than
        being flattened into one file.
        """
        day = when.strftime("%Y-%m-%d")
        if self.kind == "local":
            return root / "local" / f"{day}.log"
        return root / self.kind / self.label / f"{day}.log"


class ChatNotLoaded(MonoError):
    """The chat types exist on disk but the game has not instantiated them."""


class ChatReader:
    """Locates chat channels once, then reads their messages cheaply."""

    # ChatChannelInfo, offsets resolved from the game's own metadata
    OFF_CHANNEL_NAME = 0x10
    OFF_DISPLAY_NAME = 0x18
    OFF_PEER = 0x28
    OFF_MESSAGES = 0x50
    OFF_FOCUSED = 0x68
    OFF_IS_LOCAL = 0x69
    OFF_IS_PRIVATE = 0x6A
    OFF_IS_SYSTEM = 0x6B

    # ChatChannelMessageEventArgs
    OFF_CHANNEL = 0x10
    OFF_PARTICIPANT = 0x28
    OFF_MESSAGE = 0x30
    OFF_TIMESTAMP = 0x40
    OFF_STYLE = 0x48

    # ChannelMember
    OFF_MEMBER_NAME = 0x10

    def __init__(self, proc: Process, mono: Mono, verbose: bool = True):
        self.proc = proc
        self.mono = mono
        self.verbose = verbose
        self.klass: int | None = None
        self.message_vtable: int | None = None
        self.channels: list[Channel] = []
        self.loose_messages: list[int] = []
        self._by_address: dict[int, Channel] = {}
        self._grouped: dict[int, list[int]] = {}

    def log(self, msg: str) -> None:
        if self.verbose:
            print(msg, file=sys.stderr, flush=True)

    def _verify_offsets(self, asm: AssemblySet) -> None:
        """Check the hardcoded offsets still match the shipped metadata.

        The game updates; if a field moves, this says so loudly instead of
        silently logging garbage.
        """
        checks = [
            (CHAT_CHANNEL_INFO, "<ChannelName>k__BackingField", self.OFF_CHANNEL_NAME),
            (CHAT_CHANNEL_INFO, "m_DisplayName", self.OFF_DISPLAY_NAME),
            (CHAT_CHANNEL_INFO, "m_Messages", self.OFF_MESSAGES),
            (CHAT_CHANNEL_INFO, "<IsLocal>k__BackingField", self.OFF_IS_LOCAL),
            (CHAT_CHANNEL_INFO, "<IsPrivate>k__BackingField", self.OFF_IS_PRIVATE),
            ("ChatChannelMessageEventArgs", "<Message>k__BackingField", self.OFF_MESSAGE),
            ("ChatChannelMessageEventArgs", "<TimeStamp>k__BackingField", self.OFF_TIMESTAMP),
            ("ChatChannelMessageEventArgs", "<Style>k__BackingField", self.OFF_STYLE),
            ("ChannelMember", "<Name>k__BackingField", self.OFF_MEMBER_NAME),
        ]
        problems = []
        for type_name, field_name, expected in checks:
            t = asm.get(type_name)
            if t is None:
                problems.append(f"type {type_name} missing from metadata")
                continue
            f = next((f for f in t.instance_fields if f.name == field_name), None)
            if f is None:
                problems.append(f"{type_name}.{field_name} missing")
            elif f.predicted_offset != expected:
                problems.append(
                    f"{type_name}.{field_name} moved: expected +0x{expected:X}, "
                    f"metadata says +0x{f.predicted_offset:X}"
                )
        if problems:
            raise MonoError(
                "chat layout no longer matches the game's metadata:\n  "
                + "\n  ".join(problems)
            )
        self.log("offsets verified against shipped metadata")

    def find_message_vtable(self) -> int:
        """Locate ChatChannelMessageEventArgs by the shape of its instances.

        Searching for a class by name means finding the name in Mono's
        metadata heap and then whatever points at it - two wide sweeps, and
        fragile. We already know exactly what a chat message looks like, so
        it is far cheaper to look for that shape directly:

            +0x00  vtable        an aligned pointer
            +0x30  <Message>     a pointer to a managed string
            +0x40  <TimeStamp>   DateTime ticks in a believable range
            +0x48  <Style>       the ChatStyle enum, 0..3

        Every real message shares one vtable, so the winner is simply the
        vtable that the most shape-matching objects agree on - and it is then
        confirmed by reading its class name, so a coincidence cannot pass.
        """
        lo = _ticks(datetime(2015, 1, 1))
        hi = _ticks(datetime(2100, 1, 1))
        votes: Counter[int] = Counter()

        for region in self.mono.cached_heap_regions():
            for base, data in self.proc.read_chunks(region):
                n = len(data) // 8
                if n < 12:
                    continue
                q = np.frombuffer(data, dtype=np.uint64, count=n)
                head = q[: n - 10]
                vt = head
                msg = q[6 : n - 4]
                ticks = q[8 : n - 2] & np.uint64(0x3FFFFFFFFFFFFFFF)
                style = q[9 : n - 1] & np.uint64(0xFFFFFFFF)
                m = (
                    (vt > 0x10000) & (vt < 0x7FFFFFFFFFFF) & (vt % 8 == 0)
                    & (msg > 0x10000) & (msg < 0x7FFFFFFFFFFF) & (msg % 4 == 0)
                    & (ticks > lo) & (ticks < hi)
                    & (style <= 3)
                )
                for idx in np.flatnonzero(m):
                    votes[int(head[idx])] += 1

        for vtable, count in votes.most_common(40):
            try:
                ns, name = self.mono.class_name(self.proc.ptr(vtable))
            except (ProcessError, MonoError):
                continue
            if name == MESSAGE_TYPE:
                self.log(f"  {MESSAGE_TYPE} vtable 0x{vtable:X} ({count} instances)")
                return vtable
        raise ChatNotLoaded(
            "no chat messages found in memory. Log in and enter the world so "
            "chat has something in it, then try again (or use --wait)."
        )

    def discover_via_messages(self, asm: AssemblySet) -> list[Channel]:
        """Find messages by shape, then reach their channels through them.

        Each message's `<Channel>` field points at the ChatChannelInfo that
        owns it, so the channels fall out of the messages rather than having
        to be hunted for separately.
        """
        self._verify_offsets(asm)
        self.log("scanning the managed heap for chat messages...")
        self.message_vtable = self.find_message_vtable()
        return self.refresh_via_messages()

    def scan_messages(self) -> dict[int, list[int]]:
        """Every live message object, grouped by the channel it belongs to.

        Keyed by channel address; messages with no channel are collected
        under 0 so nothing is silently dropped.
        """
        if self.message_vtable is None:
            raise MonoError("call discover_via_messages() first")
        hits = self.mono._scan_pointers_to(
            {self.message_vtable}, regions=self.mono.cached_heap_regions()
        )
        grouped: dict[int, list[int]] = {}
        for h in hits.tolist():
            obj = int(h)
            try:
                if self.proc.ptr(obj) != self.message_vtable:
                    continue
                ch_ptr = self.proc.ptr(obj + self.OFF_CHANNEL)
            except ProcessError:
                continue
            grouped.setdefault(ch_ptr, []).append(obj)
        return grouped

    def refresh_via_messages(self) -> list[Channel]:
        """Re-collect the channels reachable from live message objects."""
        grouped = self.scan_messages()
        self._grouped = grouped
        channels: dict[int, Channel] = {}
        for ch_ptr in grouped:
            if not ch_ptr:
                continue
            ch = self._read_channel(ch_ptr)
            if ch is not None:
                channels[ch_ptr] = ch
        self.channels = list(channels.values())
        self._by_address = channels
        self.loose_messages = grouped.get(0, [])
        total = sum(len(v) for v in grouped.values())
        self.log(
            f"  {total} message object(s) -> {len(self.channels)} channel(s)"
            + (f", {len(self.loose_messages)} with no channel" if self.loose_messages else "")
        )
        return self.channels

    def _read_message(self, obj: int, label: str) -> Message | None:
        """One ChatChannelMessageEventArgs, read through the verified offsets."""
        try:
            text = self.mono.read_string_field(obj + self.OFF_MESSAGE)
            if text is None:
                return None
            when = self.mono.read_datetime(obj + self.OFF_TIMESTAMP)
            style = self.proc.i32(obj + self.OFF_STYLE)
            sender = None
            member = self.proc.ptr(obj + self.OFF_PARTICIPANT)
            if member:
                sender = self.mono.read_string_field(member + self.OFF_MEMBER_NAME)
        except (ProcessError, MonoError):
            return None
        return Message(when=when, sender=sender, text=text, style=style, channel=label)

    def poll(self) -> list[tuple[Channel, list[Message]]]:
        """Current messages per channel, newest scan.

        Reads the standalone message objects rather than each channel's
        `m_Messages` list: the client leaves that list empty in practice, so
        the objects themselves are the only complete source. Anything the
        list *does* hold is merged in, and the writer de-duplicates.
        """
        grouped = self.scan_messages()
        self._grouped = grouped
        out: list[tuple[Channel, list[Message]]] = []
        for ch_ptr, objs in grouped.items():
            if not ch_ptr:
                continue
            channel = self._by_address.get(ch_ptr) or self._read_channel(ch_ptr)
            if channel is None:
                continue
            self._by_address[ch_ptr] = channel
            msgs = [m for m in (self._read_message(o, channel.label) for o in objs) if m]
            msgs += self.messages(channel)          # whatever the list holds too
            msgs.sort(key=lambda m: (m.when or datetime.min))
            if msgs:
                out.append((channel, msgs))
        return out

    def _read_channel(self, addr: int) -> Channel | None:
        try:
            name = self.mono.read_string_field(addr + self.OFF_CHANNEL_NAME)
            display = self.mono.read_string_field(addr + self.OFF_DISPLAY_NAME)
            is_local = bool(self.proc.u8(addr + self.OFF_IS_LOCAL))
            is_private = bool(self.proc.u8(addr + self.OFF_IS_PRIVATE))
            is_system = bool(self.proc.u8(addr + self.OFF_IS_SYSTEM))
            # A channel with no name and no message list is not a real one.
            messages = self.proc.ptr(addr + self.OFF_MESSAGES)
            if not name and not display and not messages:
                return None
            peer = None
            peer_obj = self.proc.ptr(addr + self.OFF_PEER)
            if peer_obj:
                peer = self.mono.read_string_field(peer_obj + self.OFF_MEMBER_NAME)
        except (ProcessError, MonoError):
            return None
        return Channel(
            address=addr,
            name=name or "",
            display=display or "",
            is_local=is_local,
            is_private=is_private,
            is_system=is_system,
            peer=peer,
        )

    def messages(self, channel: Channel) -> list[Message]:
        """Every message currently held by a channel."""
        out: list[Message] = []
        try:
            lst = self.proc.ptr(channel.address + self.OFF_MESSAGES)
            if not lst:
                return out
            entries = self.mono.read_list(lst)
        except (ProcessError, MonoError):
            return out

        for obj in entries:
            obj = int(obj)
            if not obj:
                continue
            try:
                text = self.mono.read_string_field(obj + self.OFF_MESSAGE)
                if text is None:
                    continue
                when = self.mono.read_datetime(obj + self.OFF_TIMESTAMP)
                style = self.proc.i32(obj + self.OFF_STYLE)
                sender = None
                member = self.proc.ptr(obj + self.OFF_PARTICIPANT)
                if member:
                    sender = self.mono.read_string_field(member + self.OFF_MEMBER_NAME)
            except (ProcessError, MonoError):
                continue
            out.append(
                Message(
                    when=when,
                    sender=sender,
                    text=text,
                    style=style,
                    channel=channel.label,
                )
            )
        return out


@dataclass
class Writer:
    """Appends messages to their files, skipping ones already written."""

    root: Path
    jsonl: bool = False
    seen: set[str] = field(default_factory=set)

    def load(self) -> None:
        if STATE_FILE.exists():
            try:
                self.seen = set(json.loads(STATE_FILE.read_text(encoding="utf-8")))
            except (json.JSONDecodeError, OSError):
                self.seen = set()

    def save(self) -> None:
        # Cap the state file so a long session cannot grow it without bound.
        keep = list(self.seen)[-20000:]
        STATE_FILE.write_text(json.dumps(keep), encoding="utf-8")

    def write(self, channel: Channel, messages: list[Message]) -> int:
        fresh = [m for m in messages if m.key not in self.seen]
        if not fresh:
            return 0
        by_path: dict[Path, list[Message]] = {}
        for m in fresh:
            when = m.when or datetime.now()
            by_path.setdefault(channel.path(self.root, when), []).append(m)

        for path, group in by_path.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("a", encoding="utf-8") as fh:
                for m in group:
                    fh.write(m.render() + "\n")
            if self.jsonl:
                jpath = path.with_suffix(".jsonl")
                with jpath.open("a", encoding="utf-8") as fh:
                    for m in group:
                        fh.write(
                            json.dumps(
                                {
                                    "time": m.when.isoformat() if m.when else None,
                                    "sender": m.sender,
                                    "text": m.text,
                                    "style": STYLE_NAMES.get(m.style, str(m.style)),
                                    "channel": channel.label,
                                    "kind": channel.kind,
                                },
                                ensure_ascii=False,
                            )
                            + "\n"
                        )
        for m in fresh:
            self.seen.add(m.key)
        return len(fresh)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("-p", "--process", default="Curio")
    p.add_argument("--managed", default=str(DEFAULT_MANAGED),
                   help="path to the game's Managed folder")
    p.add_argument("-o", "--out", default=str(HERE), help="where to write logs")
    p.add_argument("-i", "--interval", type=float, default=3.0,
                   help="seconds between polls")
    p.add_argument("--rescan-every", type=int, default=20,
                   help="re-sweep for new channels every N polls")
    p.add_argument("--once", action="store_true", help="dump current chat and exit")
    p.add_argument("--jsonl", action="store_true", help="also write structured .jsonl")
    p.add_argument("--hint", default="Lovecraft",
                   help="a string present in game memory, to bootstrap Mono")
    p.add_argument("--wait", type=float, nargs="?", const=60.0, default=0,
                   help="if chat is not loaded yet, keep retrying every N "
                        "seconds (default 60) instead of exiting")
    p.add_argument("--no-cache", action="store_true",
                   help="ignore the cached Mono layout and rediscover")
    p.add_argument("-q", "--quiet", action="store_true")
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    root = Path(args.out)

    managed = Path(args.managed)
    if not managed.is_dir():
        print(f"error: no Managed folder at {managed}", file=sys.stderr)
        return 2

    try:
        proc = Process.attach(args.process, write=False)
    except ProcessError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    if not args.quiet:
        print(f"attached to {args.process} (PID {proc.pid})", file=sys.stderr)
        print("loading metadata...", file=sys.stderr)
    paths = [managed / n for n in NEEDED if (managed / n).exists()]
    asm = AssemblySet(paths)

    mono = Mono(proc, asm.assemblies[0] if asm.assemblies else None)
    # Bootstrapping and class lookup cost a couple of minutes of sweeping,
    # but their answers hold for the life of the process, so a rerun against
    # the same game session reuses them.
    cached = not args.no_cache and mono.load_cache(CACHE_FILE)
    if cached:
        if not args.quiet:
            print("reusing cached Mono layout for this game session", file=sys.stderr)
    else:
        if not args.quiet:
            print("bootstrapping Mono (one-off, ~1 min)...", file=sys.stderr)
        try:
            mono.bootstrap(args.hint)
        except (MonoError, ProcessError) as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1

    reader = ChatReader(proc, mono, verbose=not args.quiet)
    writer = Writer(root, jsonl=args.jsonl)
    writer.load()

    while True:
        try:
            channels = reader.discover_via_messages(asm)
            break
        except ChatNotLoaded as exc:
            if not args.wait:
                print(f"error: {exc}", file=sys.stderr)
                return 1
            if not args.quiet:
                print(
                    f"chat not up yet; checking again in {args.wait}s "
                    "(Ctrl+C to stop)",
                    file=sys.stderr,
                )
            try:
                time.sleep(args.wait)
            except KeyboardInterrupt:
                return 130
            if not proc.alive:
                print("game exited", file=sys.stderr)
                return 1
        except (MonoError, ProcessError) as exc:
            print(f"error: {exc}", file=sys.stderr)
            return 1

    mono.save_cache(CACHE_FILE)
    if not args.quiet:
        print(f"found {len(channels)} channel(s):", file=sys.stderr)
        for ch in channels:
            print(f"  [{ch.kind:8}] {ch.label}", file=sys.stderr)

    polls = 0
    try:
        while True:
            total = 0
            for ch, msgs in reader.poll():
                fresh = [m for m in msgs if m.key not in writer.seen]
                n = writer.write(ch, msgs)
                total += n
                if n and not args.quiet:
                    for m in fresh:
                        print(f"[{ch.kind}/{ch.label}] {m.render()}")
            if total:
                writer.save()
            if args.once:
                break
            polls += 1
            if args.rescan_every and polls % args.rescan_every == 0:
                try:
                    reader.refresh_via_messages()
                except (MonoError, ProcessError):
                    pass
            if not proc.alive:
                print("game exited", file=sys.stderr)
                break
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print(file=sys.stderr)
    finally:
        writer.save()
        proc.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())

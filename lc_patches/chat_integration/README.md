# chat_integration

Reads the Lovecraft client's chat out of memory and writes it to disk, one
file per conversation per day. Read-only — nothing is written into the game.

```bash
cd lc_patches/chat_integration
python chat_logger.py              # follow chat as it arrives
python chat_logger.py --once       # dump what's in memory right now, exit
python chat_logger.py --jsonl      # also write structured .jsonl alongside
```

## Layout

```
chat_integration/
  local/2026-09-09.log
  private/SomePlayer/2026-09-09.log
  channel/SomeGroup/2026-09-09.log
  system/GLOBAL EUROPE/2026-09-09.log
  system/GLOBAL ASIA/2026-09-09.log
  system/English/2026-09-09.log
```

Local chat is the only single stream, so it gets a file per day directly.
Everything else is a distinct conversation and gets its own folder - note the
global channels all carry `IsSystem` but are genuinely separate, so they are
kept apart rather than flattened together. Lines match how the client renders
them:

```
21:02 [some_player] : hi, anyone here?
```

Messages without a sender (notifications like *You entered new location*) are
written as `21:02 You entered new location`. Rich-text markup the client uses
for colouring is stripped from `.log`; `--jsonl` keeps the raw text.

## How it works

Messages are found by **shape**, not by class name:

| offset | field | what makes it recognisable |
|---|---|---|
| `+0x00` | vtable | an aligned pointer, shared by every message |
| `+0x30` | `<Message>` | points at a managed string |
| `+0x40` | `<TimeStamp>` | DateTime ticks in a believable range |
| `+0x48` | `<Style>` | the ChatStyle enum, 0..3 |

One heap sweep finds every object matching that shape; the vtable they agree
on is then walked to its class name to confirm it is really
`ChatChannelMessageEventArgs`. A coincidence cannot pass that check.

This replaced an earlier name-based lookup that **did not work on this
client**: the name `ChatChannelInfo` was present in the metadata heap, yet
nothing in the entire address space pointed at it, so no class record could
be found - even though instances demonstrably existed. Shape-matching sidesteps
that entirely and is far faster (one sweep instead of two per type).

Each message points at its own channel, so the channels come along for free.
Worth knowing: the client leaves `ChatChannelInfo.m_Messages` **empty** in
practice, so the standalone message objects are the real source of truth.

The client keeps a `ChatChannelInfo` object per open conversation:

| field | offset | use |
|---|---|---|
| `<ChannelName>` | `+0x10` | channel id |
| `m_DisplayName` | `+0x18` | shown name |
| `ChannelMemberOfWhomITryToCommunicateWith` | `+0x28` | the other person in a DM |
| `m_Messages` | `+0x50` | `List<ChatChannelMessageEventArgs>` |
| `<IsLocal>` / `<IsPrivate>` / `<IsSystem>` | `+0x69`/`+0x6A`/`+0x6B` | which folder it belongs in |

and each message carries `<Participant>` `+0x28`, `<Message>` `+0x30`,
`<TimeStamp>` `+0x40` and `<Style>` `+0x48`.

Those offsets come from the game's own metadata (`VWW.CoreLibs.Network.dll`,
`VWW.CoreLibs.ClientAPI.dll`) rather than from guesswork, and the script
**re-checks them against the shipped assemblies on every run**. If a game
update moves a field, it stops with an explicit message instead of quietly
logging garbage.

Two things make it fast enough to poll:

- **Boehm GC.** `mono-2.0-bdwgc` is non-moving, so a channel object's address
  stays valid for the life of the process. Discovery is a one-off sweep; each
  poll after that is a few pointer reads.
- **Heap narrowing.** The process maps ~26 GB, but managed objects live in
  about 1.1 GB of it. `Mono.heap_regions()` finds that slice in ~5s, which
  turns a four-minute sweep into ~18s.

Startup is ~2 minutes, almost all of it the one-off Mono bootstrap (~30-70s)
and class lookup (~2 min). After that, polling is cheap, and the result is
cached in `.mono_cache.json` keyed by PID so a rerun against the same client
session starts instantly.

## Tests

`python ../../tests/test_chat.py` builds byte-exact replicas of
`ChatChannelInfo`, `ChatChannelMessageEventArgs`, `ChannelMember`, `List<T>`
and `System.String` in a throwaway process and reads them back with the real
`ChatReader` — offsets, list walking, `DateTime` decoding, UTF-16 text,
folder routing, dedup and rendering. No game required, and it does not depend
on the client being in a chat state.

## "no chat messages found in memory"

Chat has to have actually run for there to be anything to read. Log in and
get into the world, then start the logger — or start it with `--wait` and it
will keep checking:

```bash
python chat_logger.py --wait          # retry every 60s until chat exists
python chat_logger.py --wait 15       # ...every 15s
```

## Notes

- Only messages still held in the client's buffers can be read — the client
  trims old history, so start the logger before a conversation you care about.
- Seen messages are tracked in `.chat_state.json` so restarting doesn't
  duplicate lines. Delete it to re-dump everything currently in memory.
- New conversations are picked up by a re-sweep every `--rescan-every` polls
  (default 20).
- Chat contains other people's messages. Where you store or share these logs
  is worth a thought, especially for DMs.

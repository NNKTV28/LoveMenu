# lcmem — memory tooling for the Lovecraft (Curio) client

A memory reader/writer for the **Lovecraft** client by The Virtual World Web Inc.
(`Curio.exe`, Unity 64-bit with the Mono scripting backend), built for
inspecting client-side state and applying community fixes.

Pure Python + numpy. Nothing is injected into the game: no DLLs, no remote
threads, no hooks. Everything works through `ReadProcessMemory` /
`WriteProcessMemory` and by reading the game's own Mono data structures.

This repository also holds **[Love Menu](love_menu_mod)**, an in-game menu
built as a BepInEx plugin. Players want that one: download
`LoveMenu-<version>.exe` from
[Releases](https://github.com/NNKTV28/LoveMenu/releases).

Documentation for both lives in the [wiki](https://github.com/NNKTV28/LoveMenu/wiki).
Licensed under [MIT](LICENSE).

## Read this first

Lovecraft is an **online game**. Its client talks to `lovecraftgame.com`, and
the assembly is full of `AccountManager`, `NetworkManager`, `ChatChannel*`,
`Room*` and Vivox voice-session types. That has a concrete technical
consequence:

**Anything the server owns cannot be changed from here.** Currency,
entitlements, inventory and progression are validated server-side. Writing a
bigger number into your local process does not grant you anything — it
desyncs your client until the next update from the server, and it is exactly
the pattern account bans are written to catch.

What this tool is genuinely good for is the client-side half: camera and FOV,
look sensitivity, UI scale, render and quality settings, frame-rate caps,
and understanding why something is misbehaving so it can be fixed. That is
also what community fixes for games like this actually consist of.

## Why this is not just a byte scanner

The game ships Mono rather than IL2CPP, and `Assembly-CSharp.dll` is
unencrypted on disk. So instead of hunting anonymous numbers, the tool reads
the real C# metadata — 1964 types, 11915 fields — and can name what it finds:

```
$ python -m lcmem type CameraFXManager
CameraFX.CameraFXManager   (extends UnityEngine.MonoBehaviour)
own fields start at +0x18

          static CameraFX.CameraFXManager           Instance
   +0x018        UnityEngine.Light                  _flashLight
   +0x020        UnityEngine.Camera                 _camera
   +0x028        u8                                 CameraFOVControl [u8]
   +0x02C        f32                                CameraFOV [f32]
   +0x030        f32                                CameraFOVSpeed [f32]
   +0x034        f32                                _defaultFov [f32]
```

At runtime it follows each object's vtable to its `MonoClass` to answer
"what *is* this address?" — so a number found by scanning can be traced back
to a named field on a named class.

## Install

Needs 64-bit Python 3.10+ and numpy. No other dependencies.

```bash
pip install numpy
python -m lcmem info
```

Run it as the same user that runs the game; elevation is not required.

## Commands

```
info                     process, modules, memory layout, metadata status
types [pattern]          list types in the assembly
type <Name>              one type's fields and predicted offsets
fields <pattern>         search fields across every type (--scannable)

scan <value>             first scan          (-t f32, -m greater/less/between)
refine <mode> [value]    narrow it           (exact changed increased ...)
results                  show current hits
setall <value>           write to every hit

read/write/dump <addr>   direct access
watch <addr>             poll and print changes
aob "48 8B ?? 89"        byte-signature search, ?? wildcards

strings <text>           find managed System.String objects
identify <addr>          what managed object is this
class <Name>             find a MonoClass
instances <Name>         find every live instance of a class
object <addr>            read an object's fields through metadata

patch list|apply|revert  declarative patches
shell                    interactive session (keeps results in memory)
```

## The scan workflow

A first scan reads ~14 GB and takes about 90 seconds; every refinement after
that is effectively instant, because it only re-reads the addresses that are
still candidates. Results persist between invocations in `.lcmem/session.npz`.

```bash
python -m lcmem scan 60 --type f32      # while the value shows 60
# change it in game
python -m lcmem refine exact 90
python -m lcmem refine decreased        # or narrow without knowing the number
python -m lcmem results
```

`shell` is better for tight loops since it keeps the snapshot in memory, and
it is the only place `scan unknown` (find a value you can't read) works:

```
lcmem> type f32
lcmem> scan unknown 512
lcmem> refine decreased
lcmem> refine unchanged
lcmem> results
```

Sessions are pinned to a PID. Restart the game and the tool tells you to
rescan rather than writing to an address that now means something else.

## Turning a hit into something durable

A bare address is worthless after a restart. Once a scan narrows to one
candidate:

```bash
python -m lcmem identify 0x1E99F58AA80      # -> CameraFX.CameraFXManager
python -m lcmem object   0x1E99F58AA80      # every field, named and decoded
```

Now you have a class and a field name rather than an address, which is what
a patch should be written against.

## Patches

Patches are JSON so a fix can be shared as a file instead of a stale address.
Three ways to locate a target:

- **`module`** — offset inside a loaded module; stable across launches.
- **`aob`** — a byte signature with `??` wildcards; survives game updates.
- **`mono`** — a class plus a field name, resolved through metadata.

```bash
python -m lcmem patch list
python -m lcmem patch apply fov
python -m lcmem patch revert           # restores every applied patch
```

Applying records the exact bytes that were overwritten in
`.lcmem/backups.json`, keyed by PID, so `revert` restores precisely what was
there and a stale backup is never replayed into a fresh process. A write that
doesn't stick is detected and rolled back, with a note that the game is
probably rewriting the value every frame — which is the usual sign you should
be patching the code that writes it, not the value itself.

`patches/community.json` ships as templates, all disabled. Verify a target
before enabling it.

## A note on predicted offsets

.NET metadata does not record instance field offsets — Mono assigns them when
the type loads — so `lcmem` reconstructs Mono's own layout algorithm. Two
rules matter, and both were confirmed against this game's live memory:

**Inheritance.** A `MonoBehaviour` subclass starts at `+0x20`: `0x10` of
object header, `UnityEngine.Object.m_CachedPtr`, then MonoBehaviour's own
`IntPtr`.

**Reference fields come first.** For a class holding GC references, Mono uses
a "GC aware" layout — two passes over the fields, placing reference-typed
ones before everything else so the collector can scan a compact run of
pointers. Declaration order is preserved *within* each pass. This is not a
detail you can skip: in `CameraFXManager`, `CameraFOV` is declared fourth but
lives at `+0x54`, after all six object references. Assuming declaration order
puts it at `+0x2C`, where it reads as `6.85e-43` — the high half of a
pointer, which is the tell-tale sign of a layout that is off.

Here is the verification, live:

```
$ python -m lcmem object 0x01E99F1C2420
0x01E99F1C2420 as CameraFX.CameraFXManager
   +0x020 UnityEngine.Light      _flashLight        = 0x1E99F455920 (UnityEngine.Light)
   +0x028 UnityEngine.Camera     _camera            = 0x1E99F52D720 (UnityEngine.Camera)
   +0x050 u8                     CameraFOVControl   = 0
   +0x054 f32                    CameraFOV          = 65.0
   +0x058 f32                    CameraFOVSpeed     = 5.0
   +0x05C f32                    _defaultFov        = 65.0
   +0x040 string                 folderName         = 'LoveCraft'
   +0x048 string                 screenshotPath     = 'C:\Users\<you>\Pictures\LoveCraft'
```

Every reference resolves to the type it is declared as, and every number is
plausible. That is the standard you should hold a layout to before writing.

It is still a **reconstruction**. Confirm with `object <addr>` first.
Prediction stops at the first field whose size isn't statically known (an
unresolvable value type), since everything after it would be guesswork —
those fields show no offset rather than a wrong one.

## Reading structured data

Beyond scalars, `lcmem.mono` can follow the game's own data structures:

```python
mono.read_list(addr)        # List<T> of references, honouring _size
mono.read_array(addr)       # T[] of references
mono.read_datetime(addr)    # System.DateTime -> Python datetime
mono.read_guid(addr)        # System.Guid
mono.read_string_field(a)   # follow a string field, tolerating null
```

Types often span assemblies — a class in `Assembly-CSharp` inheriting from
one in `VWW.CoreLibs.*`. `AssemblySet` loads several DLLs against a shared
registry so those chains resolve:

```python
from lcmem.monometa import AssemblySet
asm = AssemblySet.from_folder(managed)      # 196 assemblies, 32k types
asm.get("ChatChannelInfo")                  # resolves across DLLs
```

Parsed alone, a field typed as an enum from another DLL has unknown size and
prediction stops there; loaded as a set, it resolves.

## Speed

Two optimisations matter, both in `lcmem.mono`:

- **`heap_regions()`** — the process maps ~26 GB, but managed objects occupy
  about 1.1 GB. A two-stage sample (pointer density, then one confirmed
  vtable walk) finds that slice in ~5s and cuts an instance sweep from ~250s
  to ~18s. It is a hint, never a filter of record: if it finds nothing, the
  caller sweeps everything.
- **Session caching** — `mono.save_cache(path)` / `load_cache(path)` keep the
  discovered layout and class addresses, keyed by PID. Boehm never moves
  objects, so those stay valid for the life of the process and a rerun starts
  instantly. Every cached value is re-verified before use, since PIDs get
  reused.

## chat_integration

[`lc_patches/chat_integration`](lc_patches/chat_integration) is a worked
example built on all of the above: it reads the client's chat out of memory
and writes it to per-conversation log files, verified against the live game.
See its README.

It also turned up a limitation worth knowing about: **`find_class` cannot
always find a class by name.** On this client, `ChatChannelInfo`'s name was
present in the metadata heap but nothing anywhere pointed at it, so no
MonoClass could be located - even though instances demonstrably existed and
were reachable through another object's field. A control type resolved fine
in the same run, so the machinery works; something about how that particular
name is stored defeats it.

Where it matters, prefer finding objects by *shape* - a known field layout is
both faster (one sweep, not two) and more robust than a name lookup. See
`find_message_vtable` in the chat logger for the pattern.

## If you want to write real fixes

For a Mono game, the community-standard path is **BepInEx + Harmony**: it
loads into the game and lets you patch C# methods directly, in C#, against
the same class and field names this tool shows you. That is a far better home
for a durable fix than poking bytes. `lcmem` is the right tool for
*investigating* — finding what a value is, what owns it, and what changing it
does — and for changes that are genuinely just a number.

That BepInEx plugin lives in [`love_menu_mod/`](love_menu_mod) — see its
README.

## Layout

```
love_menu_mod/  BepInEx + Harmony plugin (C#) and its launcher; see its README
lc_patches/
  chat_integration/   reads in-game chat from memory into log files
  lc_flight/          vertical movement / unstick helper
lcmem/
  win32.py      ctypes bindings and privilege handling
  process.py    attach, modules, regions, typed read/write, Mono string reads
  scanner.py    first/refine scanning, numpy-backed, dense + sparse modes
  monometa.py   ECMA-335 parser for Assembly-CSharp.dll
  mono.py       runtime introspection: object -> vtable -> class -> name
  patches.py    declarative patches, AOB search, backup/revert
  cli.py        command line and interactive shell
tests/
  test_roundtrip.py   spawns its own target process and verifies scan+write
  test_offline.py     metadata parsing and patch handling, no game needed
  test_chat.py        chat reader against replica structures
  test_flight.py      flight locator against replica structures
patches/
  community.json      patch templates
```

`python tests/test_roundtrip.py` verifies the whole read/scan/refine/write
path against a throwaway process it creates itself — no game required.

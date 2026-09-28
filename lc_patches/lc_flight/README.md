# lc_flight

Vertical movement for the local avatar — mainly for getting out of geometry
you're stuck in.

```bash
cd lc_patches/lc_flight
python flight.py status            # read the current values, write nothing
python flight.py enable-teleport   # switch on the client's own double-click teleport
python flight.py unstick           # one upward nudge, then restore and exit
python flight.py fly               # hold Space / Ctrl to rise / sink
```

## Try `enable-teleport` first

The client already has a double-click-to-move feature, behind
`AvControl.m_DoubleClickTeleportEnabled`. If it's simply switched off, turning
it on is the least invasive fix there is for being stuck — it's a shipped
feature, not an injected behaviour, and it costs one byte.

`unstick` is the next step up: a brief upward nudge, then everything is put
back. Full `fly` is the biggest hammer and rarely what you need.

## How it works

It drives the game's **own** movement code rather than fighting physics. The
client already has all the pieces:

| field | offset | role |
|---|---|---|
| `AvControl.AxisU` | `+0x54` | the "up" input axis, next to AxisH / AxisV |
| `AvControl.LastAxisU` | `+0x58` | previous frame's value, kept in step |
| `AvControl.m_InWater` | `+0x80` | the state in which vertical input is honoured |
| `MotorBase.UpSpeed` | `+0x30` | how fast that axis moves you |
| `MotorControl.VerticalVel` | `+0xC4` | the vertical velocity gravity writes |

Two modes, and `both` (the default) applies each:

- **`swim`** — sets `m_InWater` and writes `AxisU`. Movement goes through the
  normal controller, so collision and network updates behave as usual. You're
  moving, not teleporting through walls.
- **`velocity`** — writes `MotorControl.VerticalVel` directly. Blunter, but it
  doesn't depend on the water path being reachable.

Because the game rewrites these every frame, the tool writes continuously
while a key is held (`--tick`, default 100 Hz) — it's racing the game's own
update, not replacing it.

## Finding the avatar safely

Writing to a wrong address is much worse than reading one, so the target is
established two independent ways before anything is written:

1. **Shape** — `AvControl` is recognised by its block of eight normalised
   input axes in `[-2, 2]` followed by a run of 0/1 flags; `MotorControl` by a
   live motor pointer, a vertical velocity and a move vector.
2. **Class name** — every shape match is confirmed by walking its vtable to
   the MonoClass and reading the type name back.

The test suite includes a decoy: an object with a byte-identical shape but a
different class name. It must be rejected, and is.

On top of that, `flight.py` re-checks all eight offsets against the shipped
metadata on every run and **refuses to write** if the game has moved a field.

## Restoring

Every value written is snapshotted first and restored on exit — including on
Ctrl+C, and on the toggle key turning flight off. `m_InWater` and
`m_PositionLocked` go back to what they were.

If something does go wrong, relogging resets all of it: these are runtime
values in the client's memory, nothing is written to disk or to the server.

## Options

```
-m, --mode {swim,velocity,both}   which mechanism to use (default both)
-s, --speed FLOAT                 vertical speed for velocity mode (default 6)
--tick FLOAT                      seconds between writes while held (default 0.01)
fly --up / --down / --toggle      key names (default space / ctrl / f8)
unstick -d, --duration FLOAT      seconds to nudge (default 1.5)
```

Key names: `space shift ctrl alt q e x z c f f7 f8 f9 f10 pgup pgdn home end`.

## Worth knowing

**Other players can see you.** Position in this client is sent to the server
and relayed, so flying is not a private act. There's no PvP, ranking or
economy tied to movement, so nothing is being taken from anyone — but it is
visible, and whether it's within the rules is between you and the operator.
The account risk is yours.

**The server may validate movement.** If it rejects an implausible position
you'll be snapped back. Lower `--speed` if that happens.

## Status

The offsets are verified against the shipped metadata, and the locator, field
reads/writes, decoy rejection and restore are all covered by
`python ../../tests/test_flight.py` (21 checks) against replica structures in
a process built for the purpose.

**Not yet exercised against the running game** — the client was closed when
this was written, so which of `swim` / `velocity` actually produces lift is
unconfirmed. Start with `status` to see the values move as you walk and jump,
which confirms the right object was found, then try `unstick`.

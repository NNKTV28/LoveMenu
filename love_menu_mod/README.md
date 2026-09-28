# Love Menu

A BepInEx 5 + Harmony plugin for the Lovecraft (Curio) client. It adds an
in-game menu (default key **F7**) with client-side features: fly,
speed boost, teleport, knockback immunity, body-rotation lock, wings hider,
performance and system stats, and several crash workarounds.

Full documentation: [wiki](https://github.com/NNKTV28/LoveMenu/wiki/Love-Menu).

## Install

Download `LoveMenu-<version>.exe` from
[Releases](https://github.com/NNKTV28/LoveMenu/releases) and run it.

The exe has the plugin and BepInEx 5.4.23.5 (x64, [MIT](Launcher/ThirdParty/BepInEx-LICENSE.txt))
embedded. If BepInEx is missing it asks, then installs it. It finds the game (Steam libraries, the
registry, the running game, common folders), installs the plugin, and starts
the game. If it cannot find the game, it opens a file picker for `Curio.exe`
or takes a pasted path. You can also set the path yourself:

```
LoveMenu-<version>.exe --game-dir "E:\Games\LoveCraft\Application"
LoveMenu-<version>.exe --reset-path     # forget the saved path and search again
LoveMenu-<version>.exe --find-game      # print the folder it would use, launch nothing
LoveMenu-<version>.exe --setup-only     # install BepInEx and the plugin, launch nothing
```

**The menu only activates when the game is started through the exe.** A
normal Steam launch leaves the plugin inactive. The launcher writes
`BepInEx\LoveMenu.launch` just before it starts the game; the plugin consumes
that file on startup and stays inactive without it.

## Layout

```
Plugin.cs      entry point: activation check, controllers, Unity lifecycle
Core/          shared plumbing: logging, reflection helpers, player context, crash dumps
Features/      one controller (and Harmony patch, where needed) per feature
Inputs/        rebindable keys
Settings/      persisted plugin settings
UI/            IMGUI menu, styles, theme, graphs
Launcher/      the launcher: finds the game, installs the plugin, starts it, tails the log
```

## Build

Needs the .NET SDK and a game install with BepInEx 5. The plugin compiles
against the game's own assemblies, so point it at your install. Build the
plugin first, because the launcher embeds it:

```bash
dotnet build FlyMod.csproj -c Release -p:GameDir="C:\path\to\LoveCraft\Application"
dotnet build Launcher/Launcher.csproj -c Release
```

The launcher lands in `Launcher/bin/Release/net472/LoveMenuLauncher.exe`.

## Caveat

Everything here is client-side only. Anything the server owns cannot be changed
this way — see the note at the top of the root README.

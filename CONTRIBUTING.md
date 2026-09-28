# Contributing

Thanks for helping. This page explains how to report problems, suggest
features and submit fixes for Love Menu and lcmem.

Before anything else, read the [project rules](#project-rules). Issues and pull
requests that break them are closed without review.

## Project rules

Lovecraft is an online game with other real people in it. Everything in this
repository must respect that.

1. **Client-side only.** Do not submit anything that tries to change
   server-owned state: currency, inventory, entitlements, progression,
   purchases or other accounts. It doesn't work, and it gets accounts banned.
2. **Don't harm other players.** No griefing, harassment, spam, crashing or
   lagging other clients, forcing actions on other avatars, or impersonation.
3. **Respect privacy.** No features that collect, log, export or display
   other players' messages, names or data. This is why the chat features were
   removed. Never paste other players' names or messages into an issue or pull
   request. Replace them with placeholders such as `SomePlayer`.
4. **No security bypasses.** No anti-cheat evasion, packet forging, credential
   or session-token handling, or tricks to get paid content for free.
5. **Nothing that hurts the game itself.** No DDoS, no server load testing, no
   automation that farms or spams the server.
6. **Be respectful.** Follow the [Code of Conduct](CODE_OF_CONDUCT.md).

The maintainer decides what fits the project and may close anything that does
not, with or without an explanation.

## Reporting a bug

1. Check that you are on the
   [latest release](https://github.com/NNKTV28/LoveMenu/releases), and search
   [existing issues](https://github.com/NNKTV28/LoveMenu/issues) first.
2. Open a [bug report](https://github.com/NNKTV28/LoveMenu/issues/new/choose)
   and fill in every field of the form.
3. Attach logs. They are in:
   - `Logs\` next to the launcher exe (Unity player logs, `bepinex_history.log`)
   - `BepInEx\LogOutput.log` in the game folder
   - `BepInEx\CrashDumps\` in the game folder, for freezes
4. **Remove other players' names and messages from the logs before you
   attach them.**

One problem per issue. If you find two bugs, open two issues.

## Suggesting a feature

Open a [feature request](https://github.com/NNKTV28/LoveMenu/issues/new/choose).
Describe the problem you want solved, not only the solution. Check it against
the [project rules](#project-rules) first.

## Reporting a security or privacy problem

Do not open a public issue. Email **support@nikicoding.com** with the details.

## Submitting a fix

1. For anything bigger than a small bug fix, open an issue first so we can
   agree on the approach before you spend time on it.
2. Fork the repository and create a branch from `main`, for example
   `fix/fly-sticks-after-teleport`.
3. Make the change. Keep it focused: one fix or feature per pull request.
4. Build and test (see below).
5. Open a pull request and fill in the template.

### Building

See [Building from source](https://github.com/NNKTV28/LoveMenu/wiki/Love-Menu-Building)
for Love Menu, or [lcmem](https://github.com/NNKTV28/LoveMenu/wiki/lcmem) for
the Python tools.

```bash
# Love Menu
cd love_menu_mod
dotnet build FlyMod.csproj -c Release -p:GameDir="C:\path\to\LoveCraft\Application"
dotnet build Launcher/Launcher.csproj -c Release

# lcmem
python tests/test_offline.py
python tests/test_roundtrip.py
python tests/test_flight.py
```

### What a pull request needs

- It builds with **0 errors**, and adds no new compiler warnings.
- For Love Menu changes: you tested it in game through the launcher. Say what
  you tested in the pull request.
- For lcmem changes: all tests in `tests/` pass.
- The code matches the style around it:
  - one controller per feature in `Features/`, wired up in `Plugin.cs`
  - clear, full-word names; no abbreviations
  - comments explain *why*, not *what*
- User-facing changes update the wiki page or README that describes them.
- No build output, logs, config files or personal paths in the commit.

### Commit messages

Write a short summary line in the imperative ("Fix fly speed after
teleport"), then a blank line and the reason for the change if it is not
obvious.

## License

By contributing, you agree that your contribution is licensed under the
[MIT License](LICENSE).

# Contributing to VGModAPI

Use .NET SDK 10, GNU make, and Python 3.11+. Build and package targets require your
local game/BepInEx references; pure host tests do not. Override `GAME_DIR` and
`DOTNET` in the [Makefile](../../Makefile) for your installation.

- [Contributor rules](../../CLAUDE.md)
- [Design constraints](design.md)
- [Native integration findings](native-integration.md)
- [Controlled Unity qualification](qualification-runner.md)
  - Bar scopes: [API](bar-probe-scope.md), [consumers](bar-consumer-scope.md), [linked story](bar-linked-scope.md)
  - [Absent story author](story-absence-scope.md)
- [API reference](../reference/README.md)
- [Release workflow](../../.github/workflows/release.yml) and [publisher](../../tools/publish_update.py)

Release publication requires explicit authorization. Do not run standalone publishers
concurrently or edit reserved discovery assets manually: GitHub provides no atomic
compare-and-swap for advancing those assets.

Command definitions live in the [Makefile](../../Makefile), not this guide.
`docs/reference/` and `docs/assets/` ship with the API; this directory does not.
Keep both documentation trees in plain Markdown with relative links where possible.

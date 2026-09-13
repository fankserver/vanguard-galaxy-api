# In-game e2e via Surity (alternative)

This is the **Surity-based variant** of the in-game end-to-end regression tests,
put forward as a deliberate alternative to the self-contained `EWTest` harness so
the repo can evaluate the dependency tradeoff (the sibling PR carries that
self-contained variant). The shared rationale, report protocol, save-safety rules
and current limits (session-entry automation, batch-mode support, gameplay
walk-through suites) are described below where they matter for comparison; the
authoritative shared description lives in [e2e-tests.md](e2e-tests.md) once merged.

The difference is *where the in-game runner plumbing comes from*:

| | Self-contained `EWTest` (sibling PR) | Surity-based (this PR) |
|---|---|---|
| In-game runner framework | Hand-rolled (`EWTest/`) | [Surity](https://github.com/olavim/Surity) NuGet (`Surity.Core` + `Surity.BepInEx`) |
| Debug launch | `-batchmode -nographics -runEWTests` handshake | `-batchmode -nographics -runSurityTests` handshake |
| Result delivery | Loopback listener streamed by our driver | Surity CLI streams over its adapter connection |
| Async/coroutine tests | Not yet (frame-wait helpers) | `IEnumerator` tests supported by Surity |
| Dependency | None | Third-party NuGet (dev-time only, non-shipped) |

Surity targets the same BepInEx 5 major as VGModAPI and is the established tool for
running tests inside a Unity/BepInEx game; it already provides the batchmode-launch
handshake, streamed results and coroutine support that the self-contained harness
must hand-roll — at the cost of adding a third-party dev-time dependency.

## Test content

`EWTest.Surity/` contains the VGModAPI-specific suites written as Surity
`[TestClass]`/`[Test]` classes: core-service availability, world-authoring
register→create→reconcile→remove, a minimal authored dungeon, and lifecycle session
reach. `EWTest.Surity` is a no-op BepInEx plugin so its assembly is loaded and its
tests discovered when the game is launched by the Surity CLI.

## How to run

```sh
make e2e-surity GAME_DIR='/path/to/Vanguard Galaxy' E2E_SAVE_DIR='/path/to/saves' SURITY='surity'
```

This builds and deploys `EWTest.Surity.dll` + the API + `Surity.BepInEx.dll` into
the game's plugins, then runs `tools/e2e_surity.py`, which guards the real saves
(unchanged verification) and invokes Surity, gating on its exit code (0 = pass,
non-zero = fail). Surity prints its own console results.

Prerequisites on the driver machine: the Surity CLI (`dotnet tool install
Surity.CLI`) and the `Surity.BepInEx` plugin deployed into the game. Validate the
wiring without a game: `python3 tools/e2e_surity.py --game-dir <fake> --preview`.

## Validation status

Like the self-contained variant, the `EWTest.Surity` C# is unverified source that
requires the installed game to build and run, and batch-mode support is
game-specific and must be confirmed on the game machine. The Python driver
(`tools/e2e_surity.py` + `tools/test_e2e_surity.py`) is host-verified.

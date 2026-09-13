# In-game end-to-end regression tests

The unit tests and `make check-bindings` cover the API against the game's
`Assembly-CSharp.dll` *statically*: they assert exact reflected shapes and the
accepted assembly hash, but never execute the game. A game update can therefore
change *runtime* behavior (an early return, a reordered coroutine, a member that
resolves in IL but misbehaves when called) while the signature still matches —
reflection passes, but mods break.

The end-to-end harness closes that gap. It launches the real game with the API
and a dev-only test harness, runs assertions against the **live running game**,
collects a machine-readable report, and fails on any break. Each failure names the
binding, contract or behavior to re-inspect, so an upcoming game update is not
only detected but pointed at what must be fixed.

## Architecture

The driver mirrors the proven in-game test pattern used by
[Surity](https://github.com/olavim/Surity) (a Unity-mods test framework targeting
the same BepInEx major): it launches the game with Unity's documented
standalone-player batch arguments plus a **handshake**, and the in-game harness
streams results back over a loopback connection rather than writing a shared
report file (which would be fragile across a WSL↔Windows driver/game boundary):

1. The driver binds a loopback listener and launches
   `VanguardGalaxy.exe -batchmode -nographics -runEWTests`. The `-runEWTests`
   handshake means the harness only runs when launched by the driver — never in a
   normal session.
2. The harness connects to `127.0.0.1:{port}` and streams newline-delimited JSON:
   a `meta` line, then `suite-start` / one `result` line per check, then `finish`.
3. The driver assembles a report from the stream, prints a human summary, writes
   `artifacts/e2e/report/report.json`, and returns non-zero on any live failure.

## What it covers

- **Availability** — at the main menu (no session needed): every expected service
  reports available per the supported build. A service flipping to `BindingFailed`
  or `UnsupportedGame` after an update fails immediately. This is the fastest
  detector of a hook-binding break.
- **Lifecycle** — observed `SessionStarting` → `PlayerReady` → `GameplayInitialized`
  on the disposable save, so a load/new-game path that stopped emitting events is
  caught.
- **World authoring** — a live round trip: register a pocket system, wormhole pair
  and resource site, create them anchored to the player's current system, verify
  keyed reconciliation returns the same object, then remove each and confirm the
  teardown. This exercises the deepest native integration.
- **Dungeon authoring** — acquire the provider and register a minimal valid
  authored dungeon.
- The report's `meta` records the game assembly SHA-256 and game/Unity versions so
  a run is tied to the exact build that produced it.

Gameplay walk-through suites (scripted travel to a system, accept/complete a
mission, appear at a bar, board and enter a dungeon) are the stated follow-up for
this harness; they require the session-entry automation control point described
below and are iterated on a machine that has the game.

## How to run

This is an opt-in developer tool, parallel to `make check-bindings`: it runs on
the machine that has the game installed (it needs BepInEx/Unity references and the
game itself), and it is **never part of public CI or the shipped package**.

```sh
make e2e GAME_DIR='/path/to/Vanguard Galaxy' E2E_SAVE_DIR='/path/to/saves'
```

The target builds the dev-only `EWTest` harness, deploys it (with the current API
build) into the game's `BepInEx/plugins/EWTest`, launches the game with the
handshake, waits for the streamed report at `artifacts/e2e/report/report.json`, and
fails when any live check breaks.

- `E2E_SAVE_DIR` is the real save directory. The driver never writes to it: it
  snapshots it, runs against a disposable profile, and aborts if the real tree
  changes at all.
- `E2E_TIMEOUT` (seconds, default 900) and `E2E_RUNTIME` are overridable.

To validate the wiring **without a game** (CI-safe):

```sh
python3 tools/e2e.py --game-dir <fake-game> --preview
python3 tools/e2e.py --report artifacts/e2e/report/report.json   # parse + gate a captured report
```

## Save safety

Real saves are never modified. The driver snapshots the save directory tree
(relative path → file SHA-256), runs the game against a disposable profile, and
afterwards verifies the real tree is byte-for-byte unchanged; on any divergence it
aborts. See `DisposableSaveProfile` in `tools/e2e.py` and its tests in
`tools/test_e2e.py`.

## Report protocol

The driver validates and gates the streamed messages (schema 1 report). Each check
carries `check`, `status` (`pass`/`fail`/`skip`), `message`, `expected`, `actual`,
`elapsedMs` and — on failure — a `suggestedAction` naming the binding to
re-inspect. The driver recomputes the summary from the results, so a lying summary
cannot mask a failure. The exact message framing and the report JSON are
documented in `tools/e2e.py` and enforced by `tools/test_e2e.py` against the wire
sender's expected output.

## Boundaries

- The harness uses only the public `VGModAPI.Abstractions` contract; it treats the
  game + API as a black box, which is the right posture for update-breakage
  detection. It does not reference `VGModAPI` internals or `VGModAPI.Unity`.
- `EWTest` is not part of `VGModAPI.sln` and is never built by `make test` /
  `make build`; it needs the installed game references and is built only by
  `make e2e`.
- The harness is inert unless launched with the handshake, and is never shipped.

## Current limits and follow-ups

- **Session-entry automation** is the one control point still needed to run the
  gameplay suites unattended: `make e2e` must cause the game to enter a gameplay
  session (launch into, or auto-continue, a disposable save). Until that is wired,
  reaching a session is reported as a targeted failure naming it. Do not fabricate
  reflection into the game's `SceneLoader`/menu without re-inspecting the specific
  build, per the native-integration constraints.
- **Batch mode support is game-specific and unverified**: the game must honour
  `-batchmode -nographics`. Confirm this empirically on the game machine before
  relying on headless runs.
- Gameplay walk-through suites (travel / mission / bar / boarding to a dungeon)
  are additive follow-ups to be iterated on a game machine.
- A full refresh of the supported-build checks (hash gate, `BindingCatalog`,
  reference docs) remains the owner-selected step when adopting a new game build.

## Design alternative being evaluated

This self-contained harness has no external dependency. The main architectural
alternative — using [Surity](https://github.com/olavim/Surity) directly as the
in-game runner (it already provides the batchmode launch handshake, streamed
results and coroutine test support) — is being evaluated side-by-side so the repo
can pick the approach that best fits its dependency and reliability preferences.

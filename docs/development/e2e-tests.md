# Live-game tests

`VGModAPI.E2E` is an optional BepInEx test plugin, alongside `VGModAPI.Tests`.
It consumes public API contracts; native fixture setup is confined to
`NativeSession.cs`. No test hooks or dependencies are added to the shipped API.
The project inherits the repository's nullable, language, version and
warnings-as-errors settings. It targets `netstandard2.1` for the game's Mono runtime.

## Run

With Steam running and the game closed:

```sh
make e2e
```

This builds the API and test plugin, stages only those assemblies in the game,
launches it, executes `fresh-session`, writes the result and stops the owned
process before restoring the original plugins and BepInEx configuration.
An already running game or a leftover staging backup causes refusal, not a kill
or overwrite. The controller launches a normal player with the Steam application
environment; it does not assume Unity batch/headless flags work for this game.

Use the [Makefile](../../Makefile) for configuration and command definitions:
`GAME_DIR`, `CONFIGURATION`, `E2E_PYTHON`, `E2E_SAVE_DIR`, `E2E_TIMEOUT`,
`E2E_BUILD`, `E2E_RUNTIME`, and `E2E_CASE`. The default save path is the current
Windows user's game save directory. Under WSL the target uses Windows Python
and translates paths (including spaces); the TCP listener must run on the same
OS as the game. `make e2e-build` builds/stages artifacts without changing the
installed game or launching it.

`make test` includes the controller's Python tests and the frame runner's xUnit
tests without a game. Neither it nor public CI launches E2E. The optional test
project is outside the production solution/package; `e2e-build` is its build target.
Newtonsoft.Json is an explicit dev-only dependency, not assumed to exist in the game.

## Current test

`fresh-session` uses the normal native new-player entry, marks the player
ephemeral before starting scenes, initializes the arena fixture, and asserts:

- The game's native gameplay initialization completed.
- ModAPI observes a new, unsaved session.
- Exactly one `SessionStarting → PlayerReady → GameplayInitialized` sequence
  belongs to the same session identity.
- The player remains ephemeral and no successful API save event occurred.

Vanilla `CreateTestArenaPlayer` bypasses ModAPI's new-player binding, so the
fixture uses `CreateNewGamePlayer` and mirrors the arena setup on that same
player. Fixture reflection failures name the native member; API assertions do
not bypass the API's readiness or ownership checks.

Each `TestStep` has a name, binding hint and a predicate advanced on the game
thread. Waiting, assertion exceptions and timeout failures are terminal: no
later mutation/assertion runs after a failure. The run uses a monotonic time
budget with controller time reserved for reporting. Additional gameplay cases
should exercise concrete public operations and assert observable results—not
just availability, successful registration, or skipped placeholders.

Only the fresh-session lifecycle case is implemented. This is not full API
coverage: world authoring, mission/travel/boarding gameplay and persistent
save/load round trips remain to be implemented.

## Results and safety

The runtime directory contains `player.log` and `report.json` (schema 2).
Reports contain the game/API versions, game assembly hash, results and explicit
stream completion. Each failed result carries `detail` and `binding`. Empty,
malformed, incomplete, mismatched-run or unexpected-test streams cannot pass.
A parsed failure or incomplete report returns nonzero:

```sh
python3 tools/e2e.py --report artifacts/e2e/run/report.json
```

`exitCodeBeforeCleanup` distinguishes observed process state from cleanup;
null means still running at that instant. `terminatedByController` records
whether the controller terminated its owned process after a grace period.
A game's exit code alone does not establish a test outcome. The current game's
quit handler may force its own process exit; the completed assertions and
`finish` handshake determine the test outcome.

Ephemeral state suppresses vanilla save writes. `SaveGuard` additionally hashes
the real save tree before and after the run. If the default save directory does
not exist yet, the guard verifies it remains absent; an explicitly supplied
missing save directory is rejected as a likely path error. This is a **write detector**, not a
sandbox, redirected save directory or rollback mechanism. It neither copies
nor loads existing saves. Persistent save/load tests need separate disposable
save-path isolation; do not remove the ephemeral guard to add them.

Plugins and configuration are backed up under
`<game>/BepInEx/.vgmodapi-e2e-backup` and restored on ordinary failures as well as
success. A hard controller interruption can leave that backup; the next run
refuses to overwrite it. If that happens, first verify the game is stopped,
then remove the temporary `plugins`/`config` directories and move the corresponding
backed-up directories back. If a directory did not exist before the run, there
is no corresponding backup to restore. Remove the empty backup directory only
after restoration. Never restore files beneath a process that may still be using them.

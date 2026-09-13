# In-game end-to-end tests

EWTest is an opt-in, development-only BepInEx plugin. It executes assertions
against the running game and public ModAPI contracts. It is not shipped or run
in public CI.

## Run the fresh-session test

Build the API with `make build`, then build the separate harness with
`dotnet build EWTest/EWTest.csproj -c Debug`. Stage the resulting API
assemblies and EWTest.dll in an isolated BepInEx plugin directory, preserving
and restoring the installed plugins. Do not run alongside another game instance.

Run the controller with **Windows Python** when the game runs on Windows:

```text
py -3 tools/e2e.py --game-dir "C:\path\to\Vanguard Galaxy" --save-dir "C:\path\to\Saves" --runtime-dir "C:\temp\ewtest" --suite fresh-session --launch --timeout 60
```

The controller launches a normal Unity player, not batch/headless mode. It sets
`SteamAppId` and `SteamGameId` to the game's application ID, preserving the
Steam launch context needed for direct executable startup. Steam must be running
with access to the game. The handshake and port are passed through the child
process environment. A WSL Python loopback listener is not the Windows game's
loopback listener; use Windows Python rather than assuming these are shared.

The selected test creates a new player using vanilla `CreateNewGamePlayer`,
marks it ephemeral before starting scenes, supplies the native arena setup,
and starts gameplay. It asserts:

- The native player remains ephemeral (vanilla save writes are disabled).
- Native `GameplayManager.initialized` is true.
- ModAPI emitted `SessionStarting`, `PlayerReady`, and `GameplayInitialized`
  in that order.

This is a live new-session/lifecycle test, not a complete gameplay or save/load
test. The normal new-player entry is deliberate: vanilla `CreateTestArenaPlayer`
bypasses the new-player method observed by ModAPI.

## Results and safety

The harness streams metadata, check results, and a final `finish` message over
Windows loopback. The controller writes `<runtime-dir>/report/report.json` and
returns nonzero on failed checks or an incomplete stream. A partial passing
report must not mask a timeout or disconnect. `exitCodeBeforeCleanup` records
process state before controller termination; null means it was still running,
not that it crashed or exited.

`--save-dir` hashes the existing save tree before and after execution. The
fresh-session case never loads those saves. Ephemeral state prevents vanilla
save writes; hashing detects changes but is not a sandbox or rollback facility.
The temporary profile directory is not wired to vanilla save storage. Persistent
save/load testing requires separate disposable save-path isolation.

The default `--suite all` also runs availability, lifecycle, world-authoring,
and dungeon-registration checks. These are incomplete coverage: world authoring
may skip when a provider is unavailable, configuration can fail availability,
and dungeon registration does not prove dungeon gameplay. The isolated
`fresh-session` selection does not claim those suites passed.

The harness reflects into inspected native game entry points for setup and the
native-state assertion; API assertions use public contracts. Setup exceptions
are reported as runner failures. Mutating suites require verified ephemeral
session state. The framework still needs gameplay operation scenarios and
isolated persistence tests before it can cover the full ModAPI.

# Compatibility and qualification

## Current status

**Implemented but not runtime-qualified.** Nothing has been deployed to the game. Existing mods and saves were not modified.

The adapter accepts only this inspected original `Assembly-CSharp.dll` SHA-256:

```text
a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb
```

No game-version label is inferred from old docs or assembly version numbers. The assembly hash is the current compatibility identity. A hash match is necessary for this experimental adapter but does not prove resources, loader, Unity behavior, or interactions with other mods are compatible.

On a different hash, the API service loads with unavailable lifecycle/save capabilities and applies no integration patches. Do not simply update the hash: reinspect the implementation, update tests and mappings, and qualify it.

## Evidence recorded

Local SDK: .NET SDK 10.0.111.

| Check | Result |
|---|---|
| Debug build, including example consumer | Passed, zero warnings/errors |
| Release build/package | Passed, zero warnings/errors |
| Pure state-machine, coroutine, and reflection-adapter tests | 42 passed in Debug and Release |
| Installed assembly identity, 12 method bindings, and 5 field bindings | 7 tests passed in Debug and Release |
| Harmony detour execution inside Unity | **Not run** |
| Live save/load/new-game flows | **Not run** |
| Multi-mod behavior | **Not run** |

The reflection-adapter tests use explicit small doubles. They exercise the production adapter logic but do not simulate Unity scheduling. Installed binding checks use Mono.Cecil to read metadata without loading game code. They do not execute Harmony or game methods.

Fresh source inspection also covered `Behaviour.GameManager`, `Behaviour.UI.Main.NewGame`, and `GameplayManager` to verify the new-game and manager-initialization boundaries. Decompiled game source is not redistributed.

## Repeatable commands

```sh
make build
make test
make check-bindings
make package CONFIGURATION=Release
make test CONFIGURATION=Release
make check-bindings CONFIGURATION=Release
```

Override `GAME_DIR` for another installation. `make test` needs no game installation; build/package need local BepInEx and Unity compile references, while `check-bindings` needs the original game DLL. Never use a stripped/publicized stub as proof of compatibility.

## In-game acceptance checklist — pending

Arrange owner approval before deployment. Use copied/disposable saves and the optional compiled `LifecycleObserver` example to record events. Record game/Unity/BepInEx versions, assembly hash, enabled mods, and relevant logs for each run.

- [ ] Plugin startup reports bound capabilities on the inspected DLL; no Harmony errors.
- [ ] Unsupported DLL safely reports unavailable capabilities with no patches applied.
- [ ] Load a save in space: Starting -> PlayerReady -> GameplayInitialized, one session ID.
- [ ] Load a docked save: same core sequence; do not interpret it as station-UI readiness.
- [ ] Start a new game: no premature PlayerReady during player creation/configuration.
- [ ] Return to menu, reload, and switch between two saves without restarting: invalidate old sessions and reject late signals.
- [ ] A future-version rejection or controlled invalid-copy load reports failure without a ready event.
- [ ] Manual saving produces one Started and one terminal event for the correct destination.
- [ ] Autosave rotation reports separate correct destinations/operation IDs.
- [ ] Ephemeral-player save is skipped, not successful.
- [ ] Controlled write failure/retry on disposable files produces one logical terminal outcome.
- [ ] A throwing subscriber does not suppress a healthy observer; disposal prevents future callbacks.
- [ ] Relevant existing mods coexist without changing original-call semantics or event interpretation.

Do not force disk failures, delete, or corrupt real player saves. If a scenario cannot be safely exercised, record it as unqualified rather than assuming it passed.

Known contract limits and failure handling are specified in [lifecycle-contract.md](lifecycle-contract.md). Mission events, persistence coordination, and story/UI capabilities are not implemented yet.

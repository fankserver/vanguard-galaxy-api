# Compatibility and qualification

## Current status

**Implemented but not runtime-qualified.** Owner-authorized controlled runs have used an isolated Windows game sandbox, not the normal plugin installation. The latest run correctly reports a gameplay-initialization failure; the full smoke does not pass. Existing plugins are untouched and all 37 files in the original save directory were hash-verified unchanged.

The adapter accepts only this inspected original `Assembly-CSharp.dll` SHA-256:

```text
a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb
```

The controlled runtime reports **game 0.8.2.3, Unity 6000.4.7f1, BepInEx 5.4.23.5**. These labels were observed in the sandbox startup log, not inferred from older saves or assembly metadata. The assembly hash remains the adapter identity; matching it does not prove live compatibility.

On a different hash, the API service loads with unavailable lifecycle/save capabilities and applies no integration patches. Do not simply update the hash: reinspect the implementation, update tests and mappings, and qualify it.

## Evidence recorded

Local SDK: .NET SDK 10.0.111.

| Check | Result |
|---|---|
| Debug build, including example consumer | Passed, zero warnings/errors |
| Release build/package | Passed, zero warnings/errors |
| Pure state-machine, coroutine, reflection-adapter, and hook-order tests | 46 passed in Debug and Release |
| Installed assembly identity, 12 method bindings, and 5 field bindings | 7 tests passed in Debug and Release |
| Harmony detour execution inside Unity | Exercised; missing load events fixed by callee-first installation (#27) |
| Live save/load/new-game flows | **Failed qualification**: Start → PlayerReady → SessionStartFailed; startup blocker #28; remaining scenarios not reached |
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

## Controlled run findings

On 2026-09-05, four fresh disposable sandboxes were used:

1. `qa-01`: game's fullscreen-compatibility bootstrap relaunched the process; no qualification result. The sandbox child was stopped and only its newly created FSE registry value removed.
2. `qa-02`: batch mode produced missing-input-device errors and a load timeout. Excluded as a qualification baseline.
3. `qa-03`: windowed baseline emitted only SessionStarting, even though vanilla reached scene/gameplay initialization and threw in GameplayManager.Start.
4. `qa-04`: installing the iterator factory before its caller corrected event delivery: SessionStarting → PlayerReady → SessionStartFailed. The original gameplay initialization still throws, so the smoke correctly fails rather than advancing to its save/subscriber scenarios. Quit-time saving was observed in the sandbox; this is not completion of the planned save matrix.

This isolates the missed-hook symptom to installation order in the controlled comparison; the new ordered-catalog tests protect that ordering but do not replace live evidence. [#27](https://github.com/fankserver/vanguard-galaxy-api/issues/27) tracks the defect. [#28](https://github.com/fankserver/vanguard-galaxy-api/issues/28) tracks the still-unisolated gameplay-start failure; do not yet label it a confirmed vanilla bug.

Private raw logs/fixtures remain local. The normalized event sequence above excludes original save contents and user paths. Each run's original-source hashes were rechecked unchanged. See [runner instructions and limits](qualification-runner.md) to reproduce controlled testing. The owner's full milestone test remains separate; RuntimeQualified stays false.

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

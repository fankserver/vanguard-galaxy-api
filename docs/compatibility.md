# Compatibility and qualification

## Current status

**Implemented but not runtime-qualified.** Owner-authorized controlled runs have used an isolated Windows game sandbox, not the normal plugin installation. The corrected automated smoke passes in `qa-06`; the extended sequence passes in `qa-09`, both without optional startup diagnostic hooks. This is partial runtime evidence, not completion of the broader qualification matrix. Existing plugins are untouched and all 37 files in the original save directory were hash-verified unchanged.

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
| Pure state-machine, coroutine, reflection-adapter, package-validator and hook-order tests | 74 passed in Debug; see each PR for final configuration checks |
| Installed assembly identity, 12 method bindings, and 5 field bindings | 7 tests passed in Debug and Release |
| Harmony detour execution inside Unity | Exercised; missing load events fixed by callee-first installation (#27) |
| Windows harness synthetic checks | Passed; file isolation/cleanup and typed registry snapshot/restore tested on synthetic data |
| Live save/load flows | qa-06 passed: copied docked loads, replacement, manual roundtrip, skip, exhausted retries, subscribers, valid-syntax newer-version fixture rejection without readiness, corrupt-JSON failure, recovery and quit save |
| Extended runtime cases | qa-09 passed: tutorial wizard/configuration boundary, mining-space save/load, autosave rotation, recovered transient metadata failure, and current-version empty-player rejection control |
| Empty-space/in-transit loads and delayed stale-callback injection | **Not exercised** |
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

On 2026-09-05, six fresh disposable sandboxes were used:

1. `qa-01`: game's fullscreen-compatibility bootstrap relaunched the process; no qualification result. The sandbox child was stopped and only its newly created FSE registry value removed.
2. `qa-02`: batch mode produced missing-input-device errors and a load timeout. Excluded as a qualification baseline.
3. `qa-03`: windowed baseline emitted only SessionStarting, even though vanilla reached scene/gameplay initialization and threw in GameplayManager.Start.
4. `qa-04`: installing the iterator factory before its caller corrected event delivery: SessionStarting → PlayerReady → SessionStartFailed. The original gameplay initialization still throws, so the smoke correctly fails rather than advancing to its save/subscriber scenarios. Quit-time saving was observed in the sandbox; this is not completion of the planned save matrix.

5. `qa-05`: gameplay startup succeeded with read-only diagnostics (SidePanel present), and the runner reported PASS. Log inspection rejected the future-save claim: `9999.0.0` was invalid version syntax, not a genuinely newer version. Save and PlayerPrefs preservation checks passed.
6. `qa-06`: corrected fixture `99.0.0.0`, assertions separating no-readiness completion from the observed exception path, and **no diagnostic hooks**. All ten smoke scenarios passed. Independently checked 31 events: six unique attempts, ordered invalidation/readiness, five save operations each with one terminal outcome (including quit autosave). The event does not uniquely prove the too-new-version branch: that attribution is inferred from inspected code and the fixture header, not from a current-version control fixture. The only logged exceptions were intentional metadata-write, subscriber, and corrupt-JSON failures; no gameplay NRE or version-format failure. All 37 original files were unchanged and the restored PlayerPrefs export was byte-identical to its pre-run snapshot.

This isolates the missed-hook symptom to installation order in the controlled comparison; the new ordered-catalog tests protect that ordering but do not replace live evidence. [#27](https://github.com/fankserver/vanguard-galaxy-api/issues/27) tracks the defect. [#28](https://github.com/fankserver/vanguard-galaxy-api/issues/28) records the earlier gameplay-start failure. It no longer reproduces after harness settling/safety changes, including in qa-06 without diagnostics. The precise original null reference is not proven; this is not evidence of a fixed vanilla-game bug.

The follow-up review adds explicit failure when a load request returns without its coroutine hook, plus harness safety/sequence improvements. These changes now have host/synthetic checks and the qa-06 Unity run. The optional diagnostics observe only; they never suppress an exception. Earlier runs shared PlayerPrefs without a before-snapshot, so their save hashes do not establish unchanged display settings.

Private raw logs/fixtures remain local. The normalized event sequence above excludes original save contents and user paths. Each run's original-source hashes were rechecked unchanged. See [runner instructions and limits](qualification-runner.md) to reproduce controlled testing. The owner's full milestone test remains separate; RuntimeQualified stays false.

## Extended qualification evidence

On 2026-09-05, three additional fresh sandboxes exercised the extended runner:

- `qa-07` passed recovered retry and autosave rotation, then failed a harness assertion: the current-version empty-player control raised an observed nested `NullReferenceException`, not the vanilla failure callback expected by the assertion. This is an intentional malformed-data failure, distinct from #28's earlier gameplay-start exception.
- `qa-08` passed the corrected rejection controls and native new-game wizard/configuration check. It failed the harness's assumption that a tutorial starts in the empty `Space` scene; the inspected runtime starts in `Mining`.
- `qa-09` passed all 15 scripted scenarios, with the final case explicitly limited to **mining-space save/load**, not empty-space or in-transit loading. The new-game probe observed exactly one synchronous configuration call, no PlayerReady during it, and Starting → PlayerReady → GameplayInitialized afterward. The retry test throws one deliberate metadata-write exception for its reserved sandbox filename, then observes vanilla's successful retry; it does not bypass the retry implementation. Four native autosave calls selected slots 0, 1, 2, 0 with distinct operation IDs.

The current-version control and future-version fixture have equal empty Player objects. With the current header the nested deserializer throws; with `99.0.0.0` the iterator ends without readiness. Both begin from the menu with no current player. This supplies a distinguishing control alongside source inspection, not a new public version-rejection reason contract.

Independent inspection of qa-09 verified 54 events, nine session identities (six initialized, three rejected), ordered readiness, and 11 save operations each with one terminal outcome, including quit saving. All 37 original files still match the manifest, no direct files were added/removed, and the restored PlayerPrefs export is byte-identical to the before snapshot. Raw evidence remains private. These results do not qualify mod coexistence or replace owner acceptance.

### Pending-player attribution follow-up (qa-10)

Coverage review #4 reproduced #32 in the host adapter: an untracked player replacement before a pending new game's scene request could adopt that attempt's identity. The creation finalizer now captures the created player without publishing readiness; Poll and the scene boundary reject a replacement. This adds no arena-ready capability.

`qa-10` passed all previous scenarios plus a controlled native replacement probe and recovery (17 total). From the menu, the runner calls the normal player factory, then the arena player factory without requesting arena scenes. Poll invalidates the pending attempt without readiness. A regular copied load then succeeds. This exercises **replacement detection**, not the full arena startup path or a delayed coroutine callback.

Independent inspection verified 60 events, 11 session identities, 11 paired save operations, one Starting → Invalidated replacement probe, all 37 original file hashes/direct file set unchanged, and identical before/restored PlayerPrefs exports. The pre-fix defect was reproduced under host tests; no claim is made that the pre-fix defect was reproduced in Unity. RuntimeQualified remains false.

## In-game acceptance checklist — partial (qa-10)

Arrange owner approval before deployment. Use copied/disposable saves and the optional compiled `LifecycleObserver` example to record events. Record game/Unity/BepInEx versions, assembly hash, enabled mods, and relevant logs for each run.

- [x] Plugin startup reports bound capabilities on the inspected DLL; no Harmony errors.
- [ ] Unsupported DLL safely reports unavailable capabilities with no patches applied.
- [x] Load a mining-space save: Starting -> PlayerReady -> GameplayInitialized, one session ID. Empty-space/in-transit paths remain untested.
- [x] Load a docked save: same core sequence; do not interpret it as station-UI readiness.
- [x] Start a tutorial through native wizard callbacks: no PlayerReady during the synchronous `NewGame.SaveInputs` call. Inspected `SaveInputs` invokes `GamePlayer.CreateNewGamePlayer` and completes wizard configuration before `GameManager.StartNewGame` calls `SceneLoader.LoadScenesOnStartGame`. The probe does not observe arbitrary asynchronous configuration outside that call. Pointer-driven UI acceptance remains separate.
- [x] Return to menu, reload, and switch between two saves without restarting: old sessions invalidated before replacement; no stale readiness observed.
- [ ] Explicit delayed/stale coroutine callback injection inside Unity (covered only by host tests).
- [x] A valid-syntax newer-version fixture ends without readiness; a corrupt-JSON fixture reports failure without readiness.
- [x] A current-version control with equal empty-player payload distinguishes deserialization failure from the newer-header non-readiness outcome; public events alone still do not identify a version-rejection cause.
- [x] Manual saving produces one Started and one terminal event for the correct destination.
- [x] Autosave rotation reports separate correct destinations/operation IDs.
- [x] Ephemeral-player save is skipped, not successful.
- [x] Controlled write failure with exhausted retries on disposable files produces one logical terminal outcome.
- [x] A retry recovers successfully after a transient failure.
- [x] A throwing subscriber does not suppress a healthy observer; disposal prevents future callbacks.
- [ ] Relevant existing mods coexist without changing original-call semantics or event interpretation.

Do not force disk failures, delete, or corrupt real player saves. If a scenario cannot be safely exercised, record it as unqualified rather than assuming it passed.

Known contract limits and failure handling are specified in [lifecycle-contract.md](lifecycle-contract.md). Mission events, persistence coordination, and story/UI capabilities are not implemented yet.

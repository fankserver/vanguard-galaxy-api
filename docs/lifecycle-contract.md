# Lifecycle contract — experimental 0.1.0

Implemented, automatically tested and partially exercised inside Unity; **not fully runtime-qualified**. This contract describes the supported adapter, not every possible way other mods can manipulate the game.

## Access and delivery

Reference `VGModAPI.Abstractions.dll`, declare a hard BepInEx dependency on `vgmodapi` version `0.1.0`, and obtain `ModApi.Current` in your plugin's Awake. A service can exist with unavailable capabilities: inspect `Capabilities` before relying on an integration.

All service access, subscriptions, callbacks, and subscription disposal are main-thread-only. Registration does not replay events; query `CurrentSession` for current state. Null means no observed attempt, not proof that the game is at the menu.

- Callbacks run synchronously in registration order and should be short, observational, and nonblocking.
- Do not mutate the in-progress vanilla load/save operation from a callback. Defer gameplay actions until an appropriate later boundary.
- Each subscriber exception is logged with its owner ID and does not suppress other subscribers.
- Newly registered callbacks start with the next dispatched event. Disposing a callback before its turn suppresses that invocation.
- Reentrant events are queued until current-event delivery finishes. Payload snapshots describe the event; querying current state can return a later state, especially during reentrant game actions.
- Dispose subscriptions when the consumer is destroyed. API shutdown clears subscriptions and unpatches its hooks.

## Identities

Session IDs identify runtime start attempts. They change when another observed save-load/new-game attempt starts, even for the same file. They are **not campaign IDs**, persistent identifiers, or sidecar keys.

A load's `SavePath` identifies its source file. It does not change when the user saves under a different name. Save events supply their own destination and operation ID. Save events can have a null session when no ready, matching player is tracked.

## Session events

| Event | Verified adapter boundary | Meaning / exclusions |
|---|---|---|
| `SessionStarting` | Prefix of `SaveGameFile.LoadSaveGame` or `GamePlayer.CreateNewGamePlayer` | Attempt began; not success. New-game setup may still be configuring player fields. |
| `SessionInvalidated` | Replacement attempt, menu/splash request, observed bound-player replacement, shutdown, or observer fault | Stop using prior session state. This may precede actual scene unloading. |
| `PlayerReady` | Prefix of `SceneLoader.LoadScenesOnStartGame`, attributed to an observed load coroutine or pending new-game attempt | Player has been assigned/configured and vanilla is about to request initial gameplay scenes. UI and POIs are not ready. |
| `GameplayInitialized` | Finalizer after a nonthrowing `GameplayManager.Start`, with `_initialized == true` and matching tracked player | This manager finished generating player/fleet and initializing its own gameplay state. **Not** universal world/POI/UI readiness. |
| `SessionStartFailed` | Unobserved load coroutine hook, observed load failure, nested iterator exception/disposal, load iterator ending without PlayerReady, or captured creation/scene-request/gameplay exception | Detected failure in a tracked initialization path. Not a promise to intercept every engine/async error. |

Supported progression:

```text
Starting -> PlayerReady -> GameplayInitialized
Starting or PlayerReady -> Failed
Any observed session -> Invalidated
```

Repeated signals and stale attempt IDs cannot advance or fail a replacement session. Replacement delivers invalidation before its new starting event. Failed state remains queryable until invalidated/replaced.

### Coroutine semantics

The file-load iterator and yielded child iterators are observed without flattening their yields. Attempt context exists only while advancing/disposing that iterator. Exceptions are reported and rethrown unchanged; vanilla's own catch handlers remain in charge of game behavior.

A tracked load request returning without its coroutine factory hook produces SessionStartFailed: observation is incomplete, even if vanilla continues running. This detects hook bypass; it does not assert that vanilla itself threw.

An iterator factory returning is not completion. A load that rejects a future-version save and ends without reaching PlayerReady produces a failure event.

Unity is not guaranteed to Dispose every stopped coroutine. A silently abandoned load may remain Starting until a tracked invalidation/replacement occurs. There is no guessed timeout or success event. Async scene failures outside the instrumented boundaries are not universally intercepted.

### Path-to-boundary coverage matrix

Inspected against the original assembly hash recorded in [compatibility](compatibility.md), rechecked on 2026-09-05. Member mappings are source evidence; host regressions are not Unity scheduling evidence. No new readiness capability is enabled by this matrix.

| Path and consumer need | Inspected boundary / decision | Evidence and remaining limit |
|---|---|---|
| Normal load; journals replace per-save state | `GameManager.LoadGame` unloads scenes and calls `LoadCurrentSaveGameFile` → `SaveGameFile.LoadSaveGame`. Retain tracked iterator ownership; source path is available at Starting. | qa-09 docked/mining-space loads and failure controls; Starting is not permission to persist partial state. |
| Stopped/abandoned coroutine; consumers must stop showing a pending load | An observed iterator's Dispose or exception can report failure. Unity need not call Dispose on every stop. Do not patch all `StopCoroutine` calls or add a time limit. | `DisposingUnfinishedRootReportsCancellationOnce`, `SilentlyAbandonedIteratorDoesNotInventATerminalSignal`; explicit stopped-coroutine Unity injection remains untested. Menu/replacement invalidates even without disposal. |
| Direct load bypass; external mods want cache reconstruction | `SaveGame.LoadLatestSave` reaches a tracked file load. Direct calls to the staged loader without that request lack ownership; `ObserveLoad` leaves them untouched. No synthetic session. | `UntrackedIteratorIsNotWrappedOrAssignedAReadySession`; no direct-bypass runtime qualification. A caller must use the supported file-load entry or retain its own unsupported integration. |
| Arena; ephemeral consumers want session identity | `MainMenuUI.StartTestArena` calls `CreateTestArenaPlayer`, adds storytellers, then `GameManager.StartNewGame`. The normal creator can be skipped because current player already exists. Arena remains unsupported; do not equate its later scene request with a pending unrelated new game. | #32 exposed that attribution gap in a failing host regression. Capture normal creation's player identity without readiness, then reject a foreign replacement. This does not create an arena-ready event. |
| Hot attach; late consumers want current state | Startup Poll does not infer a session from `GamePlayer.current`. Subscribing to an already running API allows querying its tracked snapshot, without replay. Attaching the API itself mid-game remains unsupported. | `MidGameAttachDoesNotGuessReadiness`, `LateSubscribersQueryWithoutReplayAndShutdownStopsDelivery`; no hot-attach Unity qualification. |
| New-game configuration failure; journals must not commit partially configured state | `NewGame.StartGame` yields before `SaveInputs`; that method calls `CreateNewGamePlayer`, then configures it before `GameManager.StartNewGame`. Creation finalizer captures identity, not readiness. Exceptions later in `SaveInputs` remain outside the API's failure hooks. | qa-09 validates the successful synchronous configuration path. `CreationMustCompleteBeforeNewGameCanBecomeReady` and stale-completion tests protect identity. No universal configuration-failure promise; wait for supported readiness and use invalidation to discard pending state. |
| Async scene error; HUD/POI consumers want usable objects | `LoadScenesOnStartGame` requests scenes; `LoadSceneIfNotLoaded` awaits `AsyncOperation.AsTask`, then sets active scene. Other `LoadScene` work is async-void. The synchronous request hook cannot catch all later failures. | Retain the narrow `GameplayManager.Start` boundary. `GameplayFailureIsNotInitializationSuccess` covers that manager, not all scene tasks. Global async-error interception and `world-ready` remain unavailable. |
| Station UI and map helpers; HUD consumers need a specific attachment target | A station interior, side panel, or POI manager has its own lifetime and can be absent after GameplayInitialized. A single global ready flag cannot express those independent lifetimes. | Defer scoped registration/teardown to milestone 05 with real UI consumers. No new signal without target lifetime, stale-target tests, and Unity evidence. |
| Foreign worker-thread calls; all consumers need main-thread callbacks | Guard faults observation; Poll reconciles capability shutdown on the main thread. Never deliver the foreign call as an off-thread lifecycle event. | `AccessFromWrongThreadIsRejected`, `ObserverFaultIsContainedAndDisablesFutureObservations`; not a general thread marshaller. |

The pending new-game identity check closes an attribution defect, not a new supported bypass path. Host tests reproduce #32; qa-10 separately exercises native factory replacement detection and normal-load recovery, not full arena startup (see compatibility evidence). Rejected paths remain explicit rather than silently widening the API.

### Unavailable coverage

- `world-ready` is deliberately unavailable. No `GameplayReady` event claims all scenes or a current POI are ready.
- Direct calls to other load routines, test-arena creation, and hot attachment to an existing session do not manufacture readiness.
- Bound-player identity changes outside supported starts invalidate the old session, but do not create a synthetic ready session.
- New-game exceptions outside the observed creation/scene/gameplay methods may require menu/replacement invalidation; there is no universal new-game error boundary yet.
- Background-thread invocation by another mod faults observation instead of delivering callbacks off-thread.

## Save outcomes

`SaveStarted` is emitted at entry to `SaveGame.Store`, after vanilla's caller already built its data snapshot. It is **not** a pre-serialization callback and must not be used to modify that snapshot.

One logical operation comprises a root Store and recognized recursive retries. Recognition requires vanilla's failure helper, the same data object, destination, format, and next attempt number. Unrelated nested saves remain distinct operations.

| Terminal event | Conditions |
|---|---|
| `SaveSucceeded` | The effective attempt returned without failure; matching `WriteSaveFile` and `WriteVersionMetadata` both returned successfully. |
| `SaveSkipped` | Vanilla's ephemeral-player guard skipped the operation. |
| `SaveFailed` | Exhausted/observed failure, escaping exception, or missing matching success evidence. |

The innermost retry establishes the outcome; unwinding parents do not duplicate events. An escaping parent exception overrides an inner success. The terminal event retains the session snapshot captured at the logical operation's start.

Success includes the game's metadata write. A later failure within Store (including event-log work) can still trigger retries/failure. The API does not claim the save was fsynced, is transactionally atomic, or will deserialize successfully later. It writes no files itself and coordinates no sidecar transaction in this milestone.

Exactly one terminal event is expected for a completed, observable Store operation. Process crashes or API observer faults can prevent a terminal notification; consumers must not infer success from its absence.

## Compatibility and faults

The runtime refuses an uninspected game assembly hash. Exact method/field binding checks run before patch installation. Each patch group is installed as a unit and rolled back on binding failure. No public contract depends on vanilla/Unity types.

`Available` means the capability's hooks bound successfully, **not** runtime qualification. `RuntimeQualified` is false for all capabilities in this version. Inspect [compatibility](compatibility.md) for evidence.

An internal observer fault stops further adapter observation and disables capabilities/invalidates the session on the next plugin update. Subscriber faults are isolated and do not trigger that shutdown. Logging failures must not propagate into vanilla.

These are observer hooks, not a conflict resolver. Other Harmony patches that skip/replace original methods, rewrite arguments, or mutate state during callbacks can invalidate the inspected semantics. Live qualification must include relevant mod combinations.

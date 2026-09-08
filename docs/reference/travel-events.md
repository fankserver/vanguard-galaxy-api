# Travel observation

Require API 0.1.9 and opt in with `[Travel] Enabled = true`. `TravelNativeAdapter` interprets native facts, `TravelPatches` installs hooks, and `ModApi.Travel`/`ModApi.Station` are exposed only when the group binds. Check `native-travel` availability before subscribing.

Bounded controlled native evidence exists for the paths below; it is not full in-game acceptance or proof that the current checkout ran in Unity. `RuntimeQualified` remains **false**. Exact candidate identities and receipts stay outside the repository. See [compatibility](compatibility.md) and [runner instructions](https://github.com/fankserver/vanguard-galaxy-api/blob/main/docs/development/qualification-runner.md).

## Leg identity and transport boundaries

Each accepted hop has a session-scoped operation ID and retains its origin and requested destination. An accepted request is not departure; an iterator factory returning is not arrival.

- `SetRouteToPOI` requests the actual in-system `waypoints[0]`, not the final `targetPoi`. A cross-system waypoint belongs to the gate/wormhole iterator. A genuine new route cancels a pending leg before requesting another, matching vanilla's `CancelTravel`-before-request order. `TravelToNextWaypoint` does not replace a pending same-leg continuation.
- Direct gate/wormhole hops request fresh legs; the current gate is not in the route list. A stale pending leg is cancelled rather than relabelled or reused merely because its origin matches.
- A loaded origin departs only on a verified `UnloadCurrentScene` origin-to-null manager transition. An already-unknown origin, where unload is a no-op, departs at the first `TravelInSystem` transport step. Jump-step origin changes also establish departure. `TravelActive()` includes preparation and is not departure evidence.
- Early cancellation at a loaded origin emits `Cancelled(origin)` without `Departed`. A zero-distance hop that never warps emits no departure; a later route supersedes that leg rather than inventing physical travel.
- In-system arrival uses attributed `SpaceshipHasArrived` scopes. Jumpgate/wormhole arrival uses `TravelJumpObserver` to advance owned nested iterators and detect destination-manager readiness before continuation can start another leg. Child yields retain Unity scheduling; a completed child does not cancel a leg. Root terminal/disposal handling owns jump cancellation.
- Final `RouteCompleted` comes from the verified `TravelToNextWaypoint` boundary with no remaining waypoints and `TravelActive()==false`, attributed to the last arrived leg. Continuation in-system legs are requested there and have distinct arrival/operation identity. Intermediate POI arrivals are not route completion.
- Jump requests use raw gate `targetSystemGuid`/`targetPoiGuid` or the wormhole waypoint target, without world/name lookup. Nominal targets absent from the current map are still valid requests. Actual arrival may differ, including tutorial world rewrites; never retrofit the request to that actual location.

Repeated departure/arrival evidence is idempotent. Nested request/arrival scopes coalesce without dropping independent managers. Old leg/session evidence cannot affect a replacement. Session reset discards queued old evidence; stale fields are ignored before validating their unused contents.

## Placement and dwell

Initial load placement is not travel arrival and carries no operation ID. A verified departure ends a known dwell interval; a verified arrival begins another. Clock rollback yields unknown dwell, not a negative or clamped value. No dwell crosses session replacement.

Cancellation after departure does not pretend the ship returned to its origin. `RecoveredPlacement` requires a previously placed session, no pending leg, unknown current location and readiness-backed native placement. It carries no operation identity, origin, requested destination or dwell, and uses mode `Unknown`. It cannot replace a known location or complete a pending leg.

Initial readiness during an accepted request is not adopted: origin/dwell stay unknown until an attributable boundary. Cancelling before departure permits initial placement again. First placement can occur after an interrupted initially unplaced leg; it is not itself a session-start event.

`CurrentLocation == null` means unknown or in transit, not proof of departure. Public `TravelTransition` separates `Origin`, `RequestedDestination` and `ActualLocation`, with session/operation identity, mode, sequence and game-time/dwell fields. Internal fact `Location` means requested destination for a request, observed location for departure/cancellation, and actual location for placement/arrival.

Location keys are opaque native system/POI identifiers; null POI means empty space. They are not provider-local creation IDs. No API-created world identity service is implied by these observations. Names use stored `MapElement._name`, never its lazy getter, so observation does not generate names or consume world randomness. Unavailable names are null; an actual empty string remains distinct. Readiness requires the exact current POI reference and a matching system/POI snapshot, not an arbitrary initialized manager.

## Station facts

Physical docking, undocking and leaving use native `Dock`, `Undock` and `EmergencyUndock` coroutine boundaries. `DockQuick` is a restore/relink operation and is not hooked.

A completed `Dock()` is eligible only with a genuine docking-request intent captured at `DockingOption.AssignSpaceshipForDocking` inside a `SpacestationExteriorManager.CheckForDocking` scope. Arrival auto-dock, the HUD dock button and idle autopilot use that scope. Intent pins the session, player, ship and option until successful root completion with physical `Docked` state; assignment itself emits nothing. Consumption yields at most one physical fact per request. Vanilla `PerformDocking` requires `CanDock()` and sets `Docking` before starting the coroutine; the consumption check is defensive, not a concurrency claim.

Restore, relink, re-init, NPC and dungeon assignments carry no intent and emit no physical docking fact. This includes `InitializePoi(init: true)`, `GameplayManager.ReinitPlayerSpaceshipRoutine`, `SpaceShip.RelinkDockedShipToStation`, `SpaceStationActions` and `DungeonOperation`, both docking-size re-init branches, and assignments whose restored spawn misses docking tolerance. Taking a real `Dock()` coroutine or lacking an interior does not turn restoration into a request.

Interior state is not a request discriminator. Arrival's `CheckForDocking` assigns an option while the ship is approaching; the synchronous `CheckForSpaceStationEnter` can open a visited station interior before `DockingOption.Update` creates the docking coroutine. `onDocked` can also open the interior before physical completion.

Undocking voids pending docking intent; a superseding/restore assignment clears it. Stale options, players or sessions cannot consume another request's intent. Undocking/leaving need no request intent: they use the captured ship's physical state and the player's actual station location.

Dock/undock factories pin immutable session/player ownership. The first step verifies that ownership is current before capturing the exact ship. Replacement before or during execution yields no facts in the replacement session; a factory created before player readiness does not manufacture a session. Genuine current-context binding errors reach `Guard` diagnostics; stale ownership is refused without faulting the replacement.

`InteriorReady` requires attributed nonthrowing `Awake` and `Start` on the exact live interior instance, station, player and session; Awake attribution is finalizer-only. No ordering is promised between `InteriorReady` and `DockedPhysical`.

## Binding and delivery

All access is main-thread-only. Subscribers and diagnostics are individually isolated; reentrant notifications queue, disposal stops delivery, and replacement rejects stale evidence. A main-thread violation faults the travel group and disables its capability/services. A stale-session operation error is reported without disabling the replacement.

`SpaceshipHasArrived` covers in-system arrival only; jumpgate/wormhole routines do not call it. Binding enumerates concrete declarations and true overrides, base-first to avoid JIT-inlining gaps. Inherited methods reuse their declaration; hidden non-overrides are not overrides. Whole-assembly type-load failure aborts resolution rather than using a partial set. Installation disables the group on failure without breaking plugin startup.

Caught nested failures cannot manufacture success. Before publication the adapter verifies live Unity objects, readiness, session/player/leg identity and observed native outcomes. Other mods bypassing these boundaries are outside the inspected semantics.

## Coverage and exclusions

These are evidence categories, not full runtime-qualification badges. Each named phase is evidence only for its own cases, with source/host/installed-metadata checks supporting distinct layers. The [runner](https://github.com/fankserver/vanguard-galaxy-api/blob/main/docs/development/qualification-runner.md) defines mandatory cases, selections, budgets, fixtures and refusal rules.

| Path | Evidence scope | Limit |
|---|---|---|
| Same-system routes, local arrivals and initial placement | Controlled `travel-in-system-station-v1` | Nested scope/stale evidence rules also have host tests; placement is not travel |
| Jumpgates and wormholes | Controlled `travel-cross-system-v1` | Arrival is sampled inside the owned jump iterator. Missing wormholes require explicit disposable native-fixture creation or the required case fails |
| Empty-origin re-route, restore/relink and both re-init docking sizes, stale coroutine replay | Controlled `travel-resilience-v1` | Genuine HUD docking is the positive control for restore silence; fixture requirements cannot be skipped into a pass |
| Post-gate continuation | Controlled `travel-recovery-continuation-v1` | `[gate, follow-on]` produces three distinct legs and one final completion; it does not exercise fast lane |
| Positive recovered placement | Controlled `travel-recovery-continuation-v1` | Live cancellation window depends on native scheduling; recovery miss-cleanup fallbacks remain host-tested only |
| Fast-lane gate chain | Controlled `travel-fast-lane-v1` | Requires `[gate, gate, destination]`, five distinct legs, one completion and observed multiplier 7 on the gate-to-gate leg, with 1 on surrounding controls. Unlock state is read, never written |
| Physical docking/undocking versus interior readiness | Controlled in-system/resilience cases | No ordering guarantee between physical docking and interior readiness; this is not exhaustive station-path qualification |
| Dwell boundaries | Controlled `travel-journal-comparison-v1` | Public dwell is checked against its same-session anchor, not against the archive's differently timed dwell |
| Tutorial exit rewrite | Source and host only | Supported raw-request/actual-arrival semantics; no native tutorial-exit phase |
| Inherited base arrival implementation | Source and host only; latent on inspected build | All six concrete POI managers declare overrides: SpacestationExterior, JumpGate, Wormhole, SpaceScene, Mining and Combat |
| Direct field mutation, teleport and cheat paths | Excluded | No owned request/transport/arrival boundary exists for attribution |

The inspected direct location writers outside transport include console teleport, `GamePatch.ResetDemoPosition`, world builders/tutorial setup and `GamePlayer.LoadGalaxySection`. Reconstruction is reported as placement, not travel. `TransitionTutorialToSandbox` is the supported rewrite within the jump path, not a blanket exclusion of tutorial arrival.

### Recovery evidence limits

The positive recovery window requires destination POI assignment and `initializedAndReady` while `TravelActive()` is still true and no arrival has run. The inspected travel wait predicate treats a missing local manager as satisfied; readiness appearing after a route silently ended cannot substitute for a live interrupted route.

The probe has bounded attempts for fixture/scheduling variability. A missed attempt must close its own abandoned leg with the player's cancel and settle the resulting placement before another attempt starts. That cleanup is not positive coverage. Unproven closure or unsettled placement ends the required case as NOT-RUN. The miss-cleanup paths remain host-tested, not natively exercised.

A timeout with a still-running route fails rather than retries. Ordinary cancel does not reset `isWarping`; residual state must be recorded, not repaired by direct field writes. A failed phase cannot provide a world for subsequent phases. An unobserved required window is a failure, never coverage.

### Actual consumers

Anima's system-visit recorder and Echo's final-route arrival snap have bounded controlled integration evidence. Their probes reuse the native route phases and validate consumer behavior separately; they do not add travel-path coverage. Echo's API-absent control checks loading and observed timing-hook execution without the API, not every automation feature. ETA-sync is separate.

TravelJournal is archived and is not a migration or integration target. Its pinned binary is used only for an explicitly selected sandbox comparison; it is not edited, rebuilt, reactivated for normal use or bridged. The comparison reads its own saved sidecars without invoking its public API/store. Its log is not ground truth: prefix timing, wormhole gaps and lazy-name side effects differ from the API's contract.

Travel observation needs no content serializer. Optional journal history belongs to its provider; these probes do not qualify automatic story/objective or world-content persistence. Full in-game acceptance remains pending.

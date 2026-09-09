# Travel observation

The travel group initializes automatically. `ModApi.Services.Travel` and `ModApi.Services.Station` are stable, non-null services after API bootstrap. Inspect their `Availability`; unavailable observation is not an empty successful route history. Subscribe with `Transitioned += handler` and remove the handler during consumer teardown. No events replay. `TravelNativeAdapter` interprets native facts and `TravelPatches` installs inspected hooks.

Availability reports binding health; current session, leg and station state remain separate. See [compatibility](compatibility.md).

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

All access is main-thread-only. Delegates and diagnostics are individually isolated; reentrant notifications queue, handler removal stops delivery, and replacement rejects stale evidence. Public session/placement queries require a matching live tracked session and available bindings. Producer shutdown closes health and rejects new event handlers. A main-thread violation faults the travel group and disables its capability/services. A stale-session operation error is reported without disabling the replacement.

`SpaceshipHasArrived` covers in-system arrival only; jumpgate/wormhole routines do not call it. Binding enumerates concrete declarations and true overrides, base-first to avoid JIT-inlining gaps. Inherited methods reuse their declaration; hidden non-overrides are not overrides. Whole-assembly type-load failure aborts resolution rather than using a partial set. Installation disables the group on failure without breaking plugin startup.

Caught nested failures cannot manufacture success. Before publication the adapter verifies live Unity objects, readiness, session/player/leg identity and observed native outcomes. Other mods bypassing these boundaries are outside the inspected semantics.

## Supported boundaries

Direct field mutation, console teleport, `GamePatch.ResetDemoPosition`, world
builders and `GamePlayer.LoadGalaxySection` do not provide an owned travel request
boundary. Reconstruction is placement, not travel. `TransitionTutorialToSandbox`
is a supported destination rewrite within the jump path.

Recovery requires destination POI assignment and `initializedAndReady` while
`TravelActive()` remains true and arrival has not run. Readiness appearing after a
route silently ended is not a live interrupted route. A missing local manager may
satisfy the native wait predicate; the API does not invent arrival or repair
`isWarping` by writing game fields.

Anima's visit history and Echo's arrival behavior are consumer responsibilities.
TravelJournal remains archived and is not an integration target. Observation needs
no content serializer; additional journal history belongs to its provider.

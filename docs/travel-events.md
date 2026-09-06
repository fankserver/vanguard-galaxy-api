# Travel observation

Issue #12 remains open. Since 0.1.9 the real adapter is implemented and installed: `TravelNativeAdapter` reduces source-grounded facts, `TravelPatches` installs native hooks, and `ModApi.Travel`/`ModApi.Station` are exposed only when the group binds. This supersedes the earlier preparatory-only status, but host tests remain synthetic; native runtime qualification and consumer reconciliation are still merge gates for closing #12.

## Adapter contract

- Each hop is a leg requested with its REAL waypoint: `SetRouteToPOI` and `TravelToNextWaypoint` request `waypoints[0]` when it is an actual in-system target (never the final `targetPoi`); a cross-system waypoint is a gate/wormhole handoff owned by the jump iterator.
- A direct gate/wormhole handoff UPGRADES a pending same-origin in-system leg into the jump leg instead of cancelling it, so consumers never see a fictitious `Requested`+`Cancelled` pair.
- Departure is the observed vacation of the origin (in-system `UnloadCurrentScene` clears the origin POI; jump steps observe the origin changing). Iterator creation is never departure.
- In-system arrival is observed through the `SpaceshipHasArrived` hierarchy with arrival scopes; jumpgate/wormhole arrival is observed by advancing owned nested iterators (`TravelJumpObserver`) and detecting destination manager readiness, captured before `TravelToNextWaypoint` can start the next leg. Nested child yields (Unity `WaitUntil`/`CustomYieldInstruction`) carry no lifecycle callbacks: only the root jump iterator terminal/disposal is cancellation, and a completed child never cancels a pending leg.
- `actualFinalRouteCompleted` is emitted once, from the verified `TravelToNextWaypoint` boundary (no remaining waypoints and `TravelActive()==false`), attributed to the leg recorded by the last arrival. Legs started by `TravelToNextWaypoint` (the in-system legs after a jump or in a fast-lane chain) are requested there, so they produce their own Arrived and can close the route.
- Departure is only a verified origin->null manager transition (`UnloadCurrentScene` NOOPs are ignored), never a method return.
- The requested destination of a jump is built from the `JumpGate`'s RAW `targetSystemGuid`/`targetPoiGuid` (or the wormhole waypoint target), with no world/name lookup, so a nominal target absent from the current (tutorial/sandbox) map is still a valid request; there is no lazy fallback that could misread a rewritten current POI as the gate. The actual destination (including tutorial rewrites) is preserved on the arrived fact, and requested/origin are retained on the leg.
- Station facts are distinct from travel legs: physical dock/undocking/leaving are observed from native `DockQuick`/`Dock`/`Undock`/`EmergencyUndock` boundaries, verified by the captured player ship's `DockingState` (Docked/Leaving), and with the initial-load `DockQuick`/`InitializePoi` suppressed as initial state rather than a transition. Interior readiness is an attributed nonthrowing `Awake`+`Start` with the exact live instance, station, player and session (Finalizer-only Awake attribution).
- Cross-session/replaced/stale evidence and nested base/override arrivals fail closed and idempotently.

## Coverage matrix

| Path | Implemented | Runtime-qualified | Notes |
|---|---|---|---|
| Same-system routes | Host adapter logic + hook | UNQUALIFIED | Campaign acceptance pending |
| In-system local POI arrival | `SpaceshipHasArrived` scopes | UNQUALIFIED | Covered by unit tests |
| Jumpgates | Owned iterator observation | UNQUALIFIED | Cross-hop requested built from raw gate guids (no world lookup) |
| Tutorial exit | Actual preserved; nominal raw | UNQUALIFIED | Host test only |
| Wormholes | Owned iterator observation | UNQUALIFIED | Nominal from waypoint target |
| Fast-lane chains | TravelToNextWaypoint requests each in-system leg | UNQUALIFIED | Each hop has its own leg |
| Initial load placement | Ready placement observed | UNQUALIFIED | Actively qualified by owner |
| Dock/undock vs interior ready | Native boundaries + DockingState verification | UNQUALIFIED | Initial-load DockQuick suppressed; no universal ordering promise |
| Direct field mutation/teleport/cheat | Excluded | — | Not treated as verified travel |

TravelJournal remains archived and must not be edited, rebuilt, reactivated, migrated or bridged. The two active integration targets are Anima's system-visit recorder and Echo's final-route arrival-snap; Echo's distinct ETA-sync remains separate. Native API hook qualification and both consumers remain merge gates for closing #12, not gates satisfied by this reducer.

## Evidence contract

An accepted request is not departure; iterator creation is not arrival. Each leg has a session-scoped operation ID and retains its requested destination. Completion requires separate observed departure and destination readiness evidence; the actual destination may differ (notably tutorial exit). Repeated departure/arrival evidence is idempotent; the adapter must coalesce nested request callbacks before creating a leg. Superseding a pending leg records cancellation before the new request; old leg/session evidence cannot affect its replacement. Session reset discards queued old evidence.

Initial placement is explicitly not travel arrival. A verified departure ends a known dwell interval and verified arrival begins another. Clock rollback produces unknown dwell, not a negative duration. Cancellation after departure does not pretend the ship returned to its origin. `RecoverPlacement` accepts readiness-backed placement after an interrupted, previously placed leg, only with no pending leg and unknown current location. It begins a new dwell interval without a request/departure/arrival fiction. It cannot replace a known location or complete a still-pending leg. Initial readiness received during an accepted request is deliberately not adopted: origin/dwell remain unknown until an attributable boundary; cancelling before departure permits initial placement again.

Internal fact `Location` means requested destination for Requested, observed location for Departed/Cancelled, and actual location for placement/arrival. The prepared public `TravelTransition` instead exposes `Origin`, `RequestedDestination` and `ActualLocation`, session/operation identity, mode, sequence and game-time/dwell fields. Placements have no operation ID; first verified placement can occur after an interrupted initially unplaced leg and is not a session-start notification. Stale-session/leg evidence is ignored before validating its unused fields.

The internal event hub enforces main-thread access, isolated subscribers/diagnostics, queued reentrancy, disposal and replacement-session rejection. `CurrentLocation == null` means unknown or in transit, not proof of departure. `RouteCompleted` is emitted from the verified `TravelToNextWaypoint` boundary and only for final-route completion, not a request or every intermediate POI arrival. A main-thread violation faults the travel group (capability/service disabled); a stale/replaced-session operation error is reported but never disables the replacement.

The `SpaceshipHasArrived` binding set covers in-system arrivals only: jumpgate and wormhole routines never call it and need their own iterator/readiness observations. Binding helpers enumerate concrete method declarations on the abstract POI base and true overrides in the inspected game assembly, base-first to prevent JIT inlining gaps. Inherited implementations reuse their declaration; hidden non-overrides are not treated as overrides. Whole-assembly type-load failure deliberately aborts resolution rather than using a partial type set; installation must catch it and disable the entire travel group, not break plugin startup. Scope coalescing prevents duplicate nested/reentrant callbacks for one manager without dropping independent managers; caught nested failures cannot manufacture success. The adapter must still verify live Unity objects, readiness, player/session/leg identity and native outcomes before publishing.

Location reads use stored `MapElement._name`, not its lazy name getter, so observation cannot generate names or consume world randomness. Both names are nullable when unavailable; an actual empty string remains distinct from unavailable. Readiness requires the exact current POI reference, not merely some initialized manager; mixed system/POI snapshots are rejected.

Location keys in the reducer are opaque native system/POI identifiers (null POI means empty space), not names or provider-local creation IDs. #21 must resolve future API-created destinations through stable owner-scoped restored world identities. Observations require no content serializer; optional journal history remains owned by the journal.

## Source coverage matrix

Fresh inspection uses original assembly SHA-256 `a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb`. This is a source map and pending delivery checklist, not a supported-path declaration.

| Path | Source boundary / hazard | Remaining implementation and evidence |
|---|---|---|
| Same-system routes | `TryInitiateTravel` can refuse; `SetRouteToPOI` sets a target synchronously. `StartTravel` can wait for undocking/extraction and preparation before `Travel`. | Capture accepted requests separately from actual departure; cancellation and in-transit load tests. |
| Local POI arrival | `Travel` assigns current POI before waiting for local readiness and calling virtual `SpaceshipHasArrived`; it can immediately start another waypoint. | Capture before chain replacement; cover every declared override plus inherited base implementations without nested duplicates. |
| Jumpgates | `JumpToSystem` yields before assigning current system/POI and requesting the destination scene. | Wrap iterator advancement with session/leg ownership; qualify actual system transition and local readiness, never factory return. |
| Tutorial exit | Jump routine can replace its nominal destination through `TransitionTutorialToSandbox`. | Preserve request separately, report actual replacement system/POI. |
| Wormholes | `JumpToWormhole` yields `TravelToWormholeDestination`, which updates location and waits for its manager. | Observe nested advancement and readiness without flattening Unity yields. |
| Fast-lane chains | Jump routine may charge the next gate and call `TravelToNextWaypoint`. | Separate leg identities; observe intermediate arrivals before starting the next leg. |
| Initial load | Reconstructed location is present before local readiness. | Emit placement, not a fabricated transit; test transit and empty-space loads. |
| Dock/undock and interior | `SceneLoader.ToggleSpaceStationInterior` starts asynchronous loading/unloading; its return does not prove interior readiness. | Inspect docking/interior owners and scoped lifetimes separately; no universal GameplayInitialized shortcut. |
| Teleport/cheat/direct field mutation | Not yet inspected for this module. | Audit concrete callers and explicitly support or exclude; do not label arbitrary pointer changes completed travel. |

TravelJournal remains archived and must not be edited, rebuilt, reactivated, migrated or bridged. The two active integration targets are Anima's system-visit recorder and Echo's final-route arrival-snap; Echo's distinct ETA-sync remains separate. The unchanged archived DLL is additionally a sandbox-only comparison history owner, not ground truth for its known prefix/override/timing gaps. A test-only recorder is not a substitute for either active consumer. Native API hook qualification and both consumers remain merge gates for closing #12, not gates satisfied by this reducer.

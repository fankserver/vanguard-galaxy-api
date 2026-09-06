# Travel observation — implementation in progress

Issue #12 remains open. The internal leg reducer is preparatory only: no public travel service, native hooks, docking/interior signal or new runtime capability is shipped by this step. Host tests are not native travel qualification.

## Evidence contract

An accepted request is not departure; iterator creation is not arrival. Each leg has a session-scoped operation ID and retains its requested destination. Completion requires separate observed departure and destination readiness evidence; the actual destination may differ (notably tutorial exit). Repeated departure/arrival evidence is idempotent; the adapter must coalesce nested request callbacks before creating a leg. Superseding a pending leg records cancellation before the new request; old leg/session evidence cannot affect its replacement. Session reset discards queued old evidence.

Initial placement is explicitly not travel arrival. A verified departure ends a known dwell interval and verified arrival begins another. Clock rollback produces unknown dwell, not a negative duration. Cancellation after departure does not pretend the ship returned to its origin. `RecoverPlacement` accepts readiness-backed placement after an interrupted, previously placed leg, only with no pending leg and unknown current location. It begins a new dwell interval without a request/departure/arrival fiction. It cannot replace a known location or complete a still-pending leg. Initial readiness received during an accepted request is deliberately not adopted: origin/dwell remain unknown until an attributable boundary; cancelling before departure permits initial placement again.

Internal fact `Location` means requested destination for Requested, observed location for Departed/Cancelled, and actual location for placement/arrival. A future public payload should expose named fields rather than inherit this internal shorthand. Stale-session/leg evidence is ignored before validating its unused fields.

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

TravelJournal remains archived and must not be edited/reactivated. The issue's two-real-consumer qualification requirement still needs a compatible integration plan that preserves journal ownership; a test-only recorder is not a substitute. Native API hook qualification and both consumers remain merge gates for closing #12, not gates satisfied by this reducer.

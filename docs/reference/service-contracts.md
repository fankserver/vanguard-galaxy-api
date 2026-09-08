# Typed service composition contracts

`ModApi.Services` exposes the API-constructed foundational service root after the
API plugin's Awake. Access before bootstrap or after shutdown throws; retained
service references report stopped state. Host tests do not establish Unity/Mono
qualification.

## Access and state

`ModServices` is sealed and API-constructed. Its lifecycle, mod inventory, save-data,
mission, travel and station references are non-null and read-only. Consumer domain
logic accepts only the interfaces it needs rather than constructing the root.

| Concern | Contract |
|---|---|
| Feature health | `IServiceStatus.Availability`, `AvailabilityChanged` |
| Session tracking versus save outcomes | Separate `ILifecycleService.SessionTracking` and `SaveOutcomes` status views |
| Current game attempt | `CurrentSession`, lifecycle `Changed`; null means no tracked attempt |
| Saved mission identity | `IMissionService.IdentityContinuity`, separate from mission observation health |
| Current location/window | Domain snapshots and events; absence does not imply a broken service |
| This mod's save data | `ISaveDataRegistration.State`, `StateChanged`, `CanRead`, `CanMutate` |
| Action permission | Live domain checks/results; never an earlier availability notification |

`ServiceUnavailableReason` is for program decisions. `Detail` is diagnostic text,
not a value to parse. Availability says nothing about full runtime qualification.
Normal session replacement does not replace service references or repair a terminal
observer fault. Session and operation handles retain their own validity boundaries.

## Notifications

All live service access and notification registration/removal is main-thread-only.

- Subscribe before reading current state. Registration does not replay; getters
  must not pump work or invoke consumer callbacks.
- Handlers run in registration order. A failure is isolated from other handlers;
  ordinary unguarded multicast invocation is not a conforming implementation.
- Removing a handler before its turn suppresses that invocation. New handlers join
  subsequent notifications; reentrant notifications queue behind current delivery.
- Commit affected dependency states and close operation gates before publishing
  notifications. An immutable event can describe an earlier state than a current
  query under reentrancy. An action must still validate current state.
- Observation subscriptions may exist while unavailable; they do not request native
  work. Unavailable operations must refuse explicitly, not silently succeed or
  queue work for later.
- Identical availability values do not notify again; a changed reason/detail does.
- A fault detected on another thread must latch the API operation gate, then notify
  on the main thread. This does not cancel already executing vanilla work or control
  a consumer's independent hooks or file writes.
- Shutdown closes gates and invalidates context before final notifications, then
  clears handlers. Retained status references report `ApiStopped`; handler removal
  and owned-handle disposal remain safe and idempotent. No automatic hot reattachment.

These are implementer requirements. The injected example tests do not constitute a
runtime notification-engine or native fault-reconciliation qualification.

## Additional custom save data

`ISaveDataService.Register(PersistenceProvider)` has an explicit result. Only
`Registered` has a non-null registration. Unavailable service, registration after a
session starts, duplicate provider, invalid provider and capacity refusal are distinct
statuses. A successful registration does not mean any session has restored data.

`SaveDataState` describes inactive, restoring, ready, blocked or disposed provider
state. Save-specific states carry the tracked session ID; a service-unavailable block
can occur before any session. Typed block reasons distinguish load/schema, restore,
capture, publication, provider removal and capacity problems.

Check `CanRead` before exposing restored values and `CanMutate` at action time.
`CanMutate` includes transient callback/save restrictions. Do not emit `StateChanged`
solely because delivering that event temporarily prevents mutation: that would cause
recursive notifications. Do not unregister/re-register on readiness changes; removal
can conservatively pause API-managed saves for other mods.

This surface stores **additional custom mod data**. It does not add serializers or
restore scheduling to API-owned story/dungeon content. Existing saved schemas and
migration rules are unchanged.

## Mod inventory

`IModInformationService.Menu` reports native menu health separately from the catalog.
`ModInventorySnapshot.Status` distinguishes no collection, partial loader-startup
inventory, a current post-startup snapshot, refresh failure and stopped inventory.
A failed refresh may retain older entries; it must not present them as newly current.
An empty successful inventory is not the same state as an uncollected inventory.
The scope remains loaded, declared API consumers—not every installed mod or proof
that a listed plugin initialized successfully.

## Consumer examples

Access inventory through `ModApi.Services.Mods`, which implements
`IModInformationService`. Saved-data integrity checks and supported schema migrations
remain required. Host-test results do not establish precompiled binary support.

The [service consumer examples](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/ServiceConsumers)
compile during host tests. They demonstrate required mission observation, a genuinely
optional assembly boundary, and additional custom save data. They are injected logic,
not deployable BepInEx plugins; the optional loader experiment uses a host load context,
not Unity/Mono. See the examples' README for their precise scope.

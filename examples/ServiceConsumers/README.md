# Injected service consumers

These plain .NET examples compile and run through the repository's host tests.
They are **not deployable BepInEx plugins**: they demonstrate injected domain logic,
not BepInEx entry points. A plugin obtains its root through `ModApi.Services` after
API startup. See the [service contract](../../docs/reference/service-contracts.md)
for access, availability and lifetime requirements.

- `MissionObserver` subscribes once, reads initial availability, ignores stale
  session facts, and unsubscribes after a terminal service or consumer failure.
  It does not require saved identity merely to observe a mission.
- `OptionalTravelObserver` has no API types in its entry-point members. A loader
  supplies no resolver when the dependency is absent or outside its supported
  version range. The guarded, non-inlined bridge contains the typed access.
  The host test refuses Abstractions resolution in an isolated load context;
  this is not a Unity/Mono missing-dependency test or a replacement for BepInEx.
- `CustomCounter` registers additional custom data before a session, observes
  provider state without re-registering, and distinguishes reading from mutation.
  Its four-byte serializer belongs to that custom data, not to API-owned content.

- `StoryReactions` subscribes to an authored definition and offers its follow-up
  through the completed mission's game. The API supplies safe delivery and execution.
- `InventoryMoves` uses game-bound inventories and transfer completion rather than
  a consumer recovery or session-token protocol.

Retain each consumer instance for its intended lifetime and dispose it during
consumer teardown. Story and inventory gameplay reactions are actionable; the
low-level observer examples remain distinct from those domain events. No consumer
frame drain, native hooks, file paths or Core references are introduced.

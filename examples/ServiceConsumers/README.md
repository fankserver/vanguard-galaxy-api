# Injected service consumers

These plain .NET examples compile and run through the repository's host tests.
They are **not deployable BepInEx plugins**: the plugin does not publish a
`ModServices` root. See the [composition contract](../../docs/reference/service-contracts.md)
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

Retain each consumer instance for its intended lifetime and dispose it during
consumer teardown. Callbacks are observational; action code still checks current
context/permission. No polling loop, native hooks, file paths or Core references
are introduced by these examples.

# Owned story authors (qualification examples)

Build both independent plugins with `make build-story-authors CONFIGURATION=Release`.
They are opt-in qualification inputs, never shipped in the API package.

`OwnedStoryCampaign` supplies a hand-authored two-step mission and declares a
`witness` decision token. `OwnedStoryJob` takes a generated pitch, destination and
reward. Both use local ID `mission-x` in separate authenticated provider assemblies.
Their `Register` methods are called by the qualification driver; neither plugin
implements persistence, serialization or a load callback. The driver uses their
public provider handles to offer/activate and query API-owned occurrences.

Native target selection, claim driving, save/load and fault injection belong to
the private qualification harness, not the public API or these authors. A travel
objective checks the native POI visit timestamp; it does not implement a dwell
timer. The example does not claim an LLM invocation or a universal mission DSL.

The initial `owned-story-v1` phase covers two authors, offered/active round trips,
native payout/idempotency, declared choices, skipped/failed saves, rollback,
cross-slot return, repeated jobs and a disposed-provider quarantine after reload.
Disposing a lease is **not** an absent-assembly test. Fresh-process provider absence,
new-game owned content and migration qualification remain separate required
work; passing this phase alone must not close that issue.

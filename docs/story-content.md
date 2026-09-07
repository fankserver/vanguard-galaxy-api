# Owned story content — foundation (#13, part 1 of the delivery)

This document describes the API-owned story contract delivered so far: the public types, the
owner-scoped identity model, the explicitly supported mission subset and the automatic persistence
of API-owned story state. **It does not yet install anything into the game.** Registration handles
record what must be installed; driving vanilla registration, reconstruction and mission acceptance
is separate work, and no runtime qualification is claimed. `#13` stays open; this foundation is not
its acceptance.

## The contract in one paragraph

A consumer first acquires its own provider lease with `IStoryApi.AcquireProvider(pluginInstance)`;
the host resolves that object to the plugin it loaded and the API derives the provider segment from
that authenticated identity. The lease registers immutable `StoryMissionDefinition`s under their own
`localId`, offers occurrences, records outcomes and answers queries — all scoped to the plugin that
acquired it. The API derives the namespaced identifier the game will store, refuses collisions,
mints a separate identity for every occurrence, retains outcomes according to the definition's
retention policy, and captures/restores that state through its OWN persistence provider. A provider
writes no codec, no save/load callback and no restoration scheduling for this state. Additional
provider-owned information keeps using the separate save-data API.

All members — including queries and disposal — are Unity-main-thread-only and enforce it.

## Supported subset, and why it is closed

Inspected in the shipped assembly for this delivery (pinned by `InstalledStoryBindingTests`):

- `MissionObjective.Create(type)` and `MissionReward.Create(type)` resolve an UNQUALIFIED type name
  under `Source.MissionSystem.Objectives` / `Source.MissionSystem.Rewards`. A provider-defined
  objective or reward type therefore cannot be reconstructed from a vanilla save at all.
- An unresolvable reward is **skipped with a log line**, so silently losing a reward is a real
  failure mode. The API refuses unsupported rewards at registration instead.
- `StoryMission.Add` assigns `allMissions[identifier]`, i.e. vanilla REPLACES an existing
  identifier. Collision detection has to live in this API.
- `Mission.FromJson` routes a string payload to `StoryMission.Get(player, id)`, whose dictionary
  index throws for an unknown identifier: a missing provider is a protected refusal, never a
  substituted mission (this matches `content-safety.md`).
- `Mission.ToJson` always writes a full object (including `storyId`, steps and rewards), and
  `GamePlayer.ToJson` writes the `missions` list, so vanilla persists ACCEPTED instances itself.
  The API owns what vanilla does not: offered content, occurrence identity, authoritative outcomes
  and declared choices.
- `AddMissionWithLog(Mission, bool)` refuses a duplicate story identifier while it is active or
  archived, so a repeated run is a LATER occurrence, never the earlier one revived.

Supported today: objectives `TravelToPoi`, `KillEnemies`, `CollectCredits`; rewards `Credits`,
`Experience`. Item and reputation rewards need owner-scoped item/faction identities and are
deliberately absent until that content path exists. This is a deliberately minimal, explicitly
defined subset, not a generic string payload model and not a universal mission DSL.

## Identity and collisions

A caller never supplies its provider name. `AcquireProvider` takes the plugin instance, the host
adapter resolves it to the plugin identity it recorded, and the segment is derived from that
identity: a readable slug of the plugin ID plus a truncated SHA-256 digest of the exact ID, so two
plugin IDs that slug identically still differ. Host plugin IDs contain dots and upper case, which
the segment charset excludes (dots must stay reserved so `vgmodapi.story.<provider>.<local>` is
unambiguous), hence the derivation rather than a direct copy. Because a truncated digest is
collision-resistant rather than provably injective, the module also records which host plugin owns
each segment and refuses a different plugin that maps onto it (`ProviderConflict`). That binding
outlives the lease: releasing a lease frees the provider's registrations, never its name.

This is a trust boundary, not a sandbox — plugins share one process and the API cannot stop
deliberate reflection. What it does prevent is one mod owning or mutating another mod's story
content by claiming its name.

`StoryContentId` segments are 1–48 lowercase ASCII letters/digits/hyphens starting with a letter —
never a path, alias or display name. The identifier is `vgmodapi.story.<provider>.<local>`, so two
independently loaded mods that both register `mission-x` produce different identifiers and neither
can capture the other's saved content. Refusals are fail-closed and diagnosed:

| Status | Meaning |
|---|---|
| `InvalidDefinition` | The definition itself is inadmissible (unsupported kind, over-long identity) |
| `DuplicateLocalId` | The same provider registered that local ID twice in this process |
| `IdentifierInUse` | The identifier already exists in this world; the API never replaces it |
| `LimitExceeded` | The bounded registry (256 definitions) is full; nothing is dropped |
| `Unavailable` | The module is disposed, or this provider lease is no longer active |

Lease acquisition itself reports `UnknownPlugin` (the host cannot resolve the caller),
`ProviderConflict` or `Unavailable`.

## Availability: which save an answer is about

A query never returns a bare empty list or a bare `false`. `StoryOccurrenceQuery` and
`StoryCompletionQuery` carry `StoryKnowledge` plus the session the answer belongs to, and
`Completed` is `null` when unavailable, so "nothing recorded in this save" can never be confused
with "this save's story state could not be read".

The module resets its ledger on `SessionStarting` and on `SessionInvalidated` /
`SessionStartFailed`, independently of any restore call, and reports `Known` only for a session
whose state it actually restored. Sessions whose owner data was blocked, corrupt, schema-unsupported
or restore-failed never receive a restore call, so they stay `Unavailable` instead of answering from
the previously loaded save. A brand-new game with no stored generation restores as known-empty. In
the unavailable state, offering or transitioning content is refused too, and the retained owner
bytes are left untouched — the module never overwrites a save it could not read with an empty
capture.

## Lifetime

The module is API-root scoped: constructed before any session starts and disposed only at API
shutdown, which is the only thing that unregisters its persistence owner (unregistering an owner
mid-session pauses coordinated saves for every registered mod). A consumer's `IStoryProvider.Dispose`
therefore releases only that provider's registrations and leaves every other owner running. A module
constructed after a session already started refuses and reports `Unavailable` until the next session
start, rather than guessing that the running save is empty.

## Occurrences, retention and limits

Every offered occurrence receives its own identity. Only the owning `StoryContentId` may activate or
retire it; a foreign provider is refused without revealing the owner's local ID. A terminal outcome
is recorded exactly once — a second attempt is refused rather than rewriting an authoritative
result. Retention is declared per definition:

- `Temporary`: keeps offered/active state plus a bounded idempotency tombstone (outcome only). No
  declared choices, no completed payload, no history.
- `Campaign`: additionally retains queryable authoritative outcomes and supported declared choices
  (at most 16 per occurrence). `IsCompleted` answers campaign completion **without MissionJournal**.

`IsCompleted` is campaign-only by definition. A temporary tombstone exists for idempotency, not as
an authoritative result, so a completed temporary job never answers `true`; it is still visible in
`Occurrences` until it falls past the horizon below.

### Retention policy

| Rule | Behaviour |
|---|---|
| `Withdraw` | Removes an OFFERED occurrence that was never accepted. Nothing was reconstructed for it, so it leaves no tombstone. Active or retired occurrences are refused. |
| Replay horizon | A temporary definition retains its newest 32 terminal tombstones; older ones are pruned. |
| Never pruned | Offered and active occurrences (needed to reconstruct live content) and every campaign entry, outcome and declared choice. |
| Beyond the horizon | A pruned occurrence reports `UnknownOccurrence`. Occurrence identities are API-generated and never reused, so a pruned job is never re-offered or resurrected under its old token. |
| Global bound | At most 2048 occurrences and 64 retained outcomes per definition. |

Bounds are refusals, never truncation. Exceeding the campaign bound diagnoses and changes nothing,
so campaign progression is never silently dropped, while a generated-job consumer cannot exhaust the
ledger and starve campaign content. Storing an outcome or a declared choice value is not narrative
history; mod-specific decisions stay mod logic.

## Automatic persistence

The module registers the reserved persistence owner `vgmodapi.story-content` (schema 1) with its own
capture/restore/validate. The payload is a bounded `VSC1` binary record set written with netstandard
binary IO and STRICT UTF-8 only (invalid bytes and unpaired surrogates are refused on both read and
write, never decoded to replacement characters) — no JSON library is introduced. The occurrence
sequence is the authoritative timeline and must be positive, unique and strictly increasing on both
sides. Payloads are capped well below the 1 MiB
envelope bound; truncated, extended, malformed or newer-version payloads are refused.

The existing coordinator rules apply unchanged: state is restored only for a generation matching the
exact loaded save, absence means no known state, and corrupt/unsupported data blocks that owner
instead of restoring empty state. Restoring an older generation REPLACES the ledger, so a newer
snapshot's completion cannot leak into an older loaded save. When persistence is unavailable or
paused, offering new content is refused with a diagnostic rather than silently accepting a
persistent mission that would not be saved.

Unregistering a definition stops offering new content. It never rewrites or deletes saved
occurrences: removal of persisted references follows `content-safety.md`, and provider-required
content still needs its provider.

## Boundary and status

Delivered here: public contracts, authenticated provider leases, session-scoped availability,
identity policy, registry, occurrence ledger with its retention policy, bounded codec, automatic
persistence registration, host tests and installed-assembly pins. Nothing constructs the module at
runtime yet, so no consumer is exposed to this surface until the native slice lands. **Not** delivered here:
installing definitions into `StoryMission`, reconstructing offered/active content in a live session,
driving acceptance/outcome transitions from observed native boundaries, the two-consumer
demonstration and the native qualification pilot. Those remain required for #13, and
`RuntimeQualified` stays false.

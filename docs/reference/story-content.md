# Owned story content

This document describes the public types, owner-scoped identity model, supported mission subset,
automatic persistence, native registration, reconstruction and acceptance of API-owned story content.
Controlled in-game evidence covers the bounded scenarios described below; it does not qualify the
entire API or every possible content combination.

## The contract in one paragraph

A consumer first acquires its own provider lease with `IStoryService.AcquireProvider(pluginInstance)`,
called DIRECTLY from that plugin's own assembly; the implementation captures the calling assembly at
that boundary, the host resolves the instance to the plugin it loaded, and the API derives the
provider segment from that authenticated identity. The lease registers immutable `StoryMissionDefinition`s under their own
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

Supported today: objectives `TravelToPoi`, `CollectCredits`, `Scripted`; rewards `Credits`,
`Experience`. Item and reputation rewards need owner-scoped item/faction identities and are
deliberately absent until that content path exists. This is a deliberately minimal, explicitly
defined subset, not a generic string payload model and not a universal mission DSL. `KillEnemies`
is refused because its native dependency and serialization requirements are not supported.

## Identity and collisions

A caller never supplies its provider name, and the argument alone does not decide the identity.
`AcquireProvider` is a non-inlined entry point that reads `Assembly.GetCallingAssembly()`; interface
dispatch adds no frame, so that is the assembly of the code which actually made the call. The host
adapter resolves the passed instance to the plugin identity it recorded, including the assembly it
loaded that plugin from, and the module refuses with `CallerMismatch` unless the two are the same
assembly. There is no caller-supplied assembly or provider parameter to spoof. The segment is then
derived from that identity: a readable slug of the plugin ID plus a truncated SHA-256 digest of the exact ID, so two
plugin IDs that slug identically still differ. Host plugin IDs contain dots and upper case, which
the segment charset excludes (dots must stay reserved so `vgmodapi.story.<provider>.<local>` is
unambiguous), hence the derivation rather than a direct copy. Because a truncated digest is
collision-resistant rather than provably injective, the module also records which host plugin owns
each segment and refuses a different plugin that maps onto it (`ProviderConflict`). That binding
outlives the lease: releasing a lease frees the provider's registrations, never its name.

This is an ordinary-use boundary, NOT a sandbox. It stops a different plugin assembly from taking
another mod's provider identity by passing that mod's instance. It does not stop reflection,
injected code, or an assembly that itself declares several plugins: association is at assembly
granularity, so such an assembly can acquire any of its own plugins' segments. Plugins share one
process, and nothing here contains a determined mod.

The runtime host adapter resolves the instance against loaded BepInEx plugins and supplies the
assembly associated with that instance. The module enforces that association before issuing a lease.

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

Lease acquisition itself reports `UnknownPlugin` (the host cannot resolve the instance),
`CallerMismatch` (the instance belongs to a plugin from another assembly), `AlreadyAcquired`,
`ProviderConflict`, `LimitExceeded` (all 32 provider slots are bound) or `Unavailable`.

A lease is not shared and not reference counted. While one is live, a second `AcquireProvider` from
the same plugin is refused with `AlreadyAcquired` and returns no handle, so one holder's `Dispose`
can never revoke another holder's registrations; callers cache the lease they acquired. After
disposal a fresh lease is issued, the old handle stays inactive, and persisted occurrences survive —
releasing a lease never deletes saved data.

## Availability: which save an answer is about

A query never returns a bare empty list or a bare `false`. `StoryOccurrenceQuery` and
`StoryCompletionQuery` carry `StoryKnowledge` plus the session the answer belongs to, and
`Completed` is `null` when unavailable, so "nothing recorded in this save" can never be confused
with "this save's story state could not be read".

The module resets its ledger on `SessionStarting` and on `SessionInvalidated` /
`SessionStartFailed`, independently of any restore call, and reports `Known` only for a session
whose state it actually restored AND whose owner still holds readable state for it. Owner readiness
is re-read on every answer, not only before the restore: an owner whose data was blocked, unreadable
or restore-failed holds accepted state that will not reach disk, so queries report `Unavailable`
until it recovers.

Reading and mutating are gated separately, because they are not the same risk. While lifecycle
callbacks are dispatching, or a save is already in flight, the restored state is perfectly readable
and queries keep answering `Known` — rediscovering content from a `GameplayInitialized` callback,
which is the documented way to use this API after a reload, works. A MUTATION in those moments is
refused with `StoryTransitionStatus.Busy` and a diagnostic naming the real reason, because content
accepted then would not be part of the save being written. `Busy` is temporary: the same call
succeeds once the callback or save completes. It is deliberately distinct from `Unavailable`, which
means the module has no trustworthy state for this session at all. Sessions whose owner data was blocked, corrupt, schema-unsupported
or restore-failed never receive a restore call, so they stay `Unavailable` instead of answering from
the previously loaded save. A brand-new game with no stored generation restores as known-empty. In
the unavailable state, offering or transitioning content is refused too, and the retained owner
bytes are left untouched — the module never overwrites a save it could not read with an empty
capture.

## Lifetime

The module is API-root scoped: constructed before any session starts and disposed only at API
shutdown, which is the only thing that unregisters its persistence owner (unregistering an owner
mid-session pauses coordinated saves for every registered mod). A consumer's `IStoryProvider.Dispose`
therefore releases only that provider's registrations and leaves every other owner running.

Construction before the first session is a PRECONDITION, not a soft state: the persistence
coordinator refuses a new owner once a session is live, so the constructor checks the lifecycle
first and throws `InvalidOperationException`. It throws before registering anything, so a refused
construction leaves no owner, no subscription and no paused coordinator behind for other mods.

## Occurrences, retention and limits

Every offered occurrence receives its own identity. Only the owning `StoryContentId` may activate or
retire it; a foreign provider is refused without revealing the owner's local ID. A terminal outcome
is recorded exactly once — a second attempt is refused rather than rewriting an authoritative
result. Retention is declared per definition:

Reserved identifiers observed in the loaded world are session-scoped like the ledger: they are
dropped at a session boundary and re-evaluated against the next world, so an identifier that exists
in one save never produces a spurious `IdentifierInUse` in another. Releasing a provider lease does
not drop them, because the current world still owns those identifiers.

- `Temporary`: keeps offered/active state plus a bounded idempotency tombstone (outcome only). No
  declared choices, no completed payload, no history.
- `Campaign`: additionally retains queryable authoritative outcomes and the choices the definition
  DECLARED. `IsCompleted` answers campaign completion **without MissionJournal**.

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
| Per-provider occurrence quota | At most 64 occurrences per bound provider (2048 / 32 providers). |
| Per-provider payload budget | 16,383 bytes of persisted state per bound provider, INCLUDING the space reserved for outcomes still to be recorded. The 32 shares plus the header fit inside the 512 KiB payload cap. |
| Global payload backstop | The whole ledger's reserved footprint, header included, must stay inside the 512 KiB payload, checked at `Offer` and on decode. This is what holds when a restored save carries rows from providers that are no longer loaded: they are never pruned and hold no lease, so more provider namespaces can exist in a save than can be bound at once. |
| Per-definition bound | At most 48 CAMPAIGN occurrences per definition, counting retired outcomes and unresolved occurrences alike, refused at `Offer`. Campaign outcomes are never pruned, so the slot is taken when the occurrence is admitted and never at retirement. |
| Provider bound | At most 32 bound providers. A further provider is refused rather than handed a share that would come out of a bound provider's retained history; bindings last for the module's lifetime. |
| Sequence bound | The occurrence sequence is checked against a bound with reserved headroom on every offer and on decode, so the timeline can never wrap. |
| Global bound | 2048 occurrences overall, as a backstop behind the per-provider quota. |

All supported schemas validate the reserved footprint, not only encoded bytes. Invalid state makes
the owner unavailable and preserves its bytes; nothing is silently truncated. Empty objective layouts
add no wire or quota overhead, so valid schema 2 snapshots at the provider or global boundary remain
readable and capturable.

Bounds are refusals, never truncation. Exceeding one diagnoses and changes nothing, so campaign
progression is never silently dropped, and because each provider's share is reserved, a
generated-job consumer can exhaust only its OWN quota. That reserved share is guaranteed for up to
32 provider namespaces IN THE SAVE, counting historical providers that are no longer loaded as well
as the ones bound right now. Beyond that the remaining global capacity is what is left, and a new
provider is refused rather than served by deleting another provider's campaign history, which is
never pruned to make room. Storing an outcome or a declared choice value
is not narrative history; mod-specific decisions stay mod logic.

### Declared choices and reserved outcome capacity

A campaign definition DECLARES its choice keys when it is constructed. That is what makes recording
an outcome a guarantee rather than a hope: offering an occurrence reserves the worst-case persisted
size of those choices out of the provider's own budget, so an admitted occurrence can always be
retired, even when every other provider has filled its budget. An occurrence that cannot reserve
that space is refused at `Offer`, before anything is recorded, instead of being stranded active with
an outcome that could never be written.

| Limit | Value |
|---|---|
| Declared choice keys per definition | 8 |
| Encoded bytes per choice key | 32 |
| Encoded bytes per choice value | 64 (a decision token such as `spared-captain`, not narrative text) |
| Reserved bytes per occurrence | at most 1024; a definition whose declared keys need more is refused at construction |

Bounds are ENCODED bytes on both sides of the codec, so a byte budget means the same thing
everywhere. A choice key the definition did not declare is refused at retirement, an oversized value
is refused, and both refusals leave the occurrence able to record its declared outcome. The supplied
collection is copied ONCE, after the caller has been authorised for that occurrence and before any
of it is validated, so what was checked is exactly what is stored: a collection whose contents change
between reads, whose `Count` disagrees with what it yields, that repeats a key, that never ends or
whose enumerator throws is refused outright, and nothing is recorded.

Reading that collection runs the CALLER's code on the game's thread, so the module re-establishes
its preconditions afterwards: lease, owner status and expected session are checked again, and the
occurrence's state is read again, before the outcome is applied. A caller that disposes its lease,
retires the same occurrence itself, or reloads the save from inside its own collection gets
`Unavailable`, `InvalidTransition` or `StaleSession` respectively, the intended retirement is
recorded exactly once, and nothing is applied on top of it. The
reservation is PERSISTED with the occurrence, so a reload restores exactly the same remaining
capacity without needing the definition to be registered first. Recording an outcome releases
whatever part of the reservation it did not use; a worst-case outcome releases nothing, which is the
honest consequence of reserving for it.

**Choice capacity is fixed by the definition as it was at OFFER time.** The reservation is persisted,
the declared key set is not. If a provider revises a definition between sessions — more keys, or
longer ones — a key that the CURRENT definition declares can still exceed the reservation an older
occurrence carries. That is refused without mutating anything, and the outcome itself remains
recordable with fewer or no choices; a refusal is never a stranded occurrence. So the honest rule is
not "only undeclared keys fail": a legitimately declared key can fail on a pre-revision occurrence.
Providers that intend to keep unresolved content across a save need STABLE definitions. Migrating a
changed choice contract for already-persisted occurrences is not attempted here and is not silently
performed; versioned-content migration is not implemented.

With the maximum declared payload a provider can hold roughly 18 unresolved campaign occurrences at
once out of its 64-occurrence quota, and considerably more with smaller declared choices or with
temporary content, which reserves nothing. Those are the honest limits: the API refuses content it
could not finish rather than admitting it and failing later.

## Finding your own occurrences again, and the session they belong to

The API owns this state, so it also hands it back. `Occurrences(localId)` returns the RETIRED
records, and `Unresolved(localId)` returns the still offered or active ones as immutable
`StoryOccurrenceSnapshot` values carrying identity, stage and retention. The snapshot type describes
an unresolved occurrence only, so it has no outcome and no choices at all: a recorded outcome with
its declared choices is a `StoryOccurrenceRecord` from `Occurrences`. A provider therefore never has to store occurrence identities in its own save
data to activate, withdraw or retire its content after a reload. Both answers carry `StoryKnowledge`
and the session they describe, and are empty when unavailable.

Every mutation states the session it believes it is acting in:
`Offer(expectedSessionId, localId)`, `Activate`, `Withdraw` and `Retire` all take it, and it is
checked before any occurrence or definition is even looked up. This matters because occurrence
identities are restored UNCHANGED: after reloading the same save, an old identity still addresses the
same occurrence, so a delayed callback from the pre-reload world could otherwise record an outcome
that the loaded save never produced. A mismatch is refused with `StaleSession`, nothing is mutated,
and the caller simply re-reads the current session from a query. Registration is not session-scoped;
definitions belong to the process and its leases.

## Keyed scripted objectives

`StoryObjective.Scripted(key, description, requiredAmount)` uses vanilla `TriggerObjective`
serialization with trigger `None`. Its stable identity is `StoryObjectiveId(definition, occurrence,
key)`, not its description or step index. Vanilla broadcast triggers cannot advance API-owned scripted
objectives; an acquired provider exposes `IStoryObjectiveProvider.SetProgress(session, identity,
absoluteProgress)`. Progress is monotonic within an attempt. Repeated values do not increment it;
inactive steps, stale sessions, foreign identities and replaced native objects are refused. A verified
native retry resets progress but preserves occurrence and key identity. Mission rewards still require
vanilla completion; setting objective progress never directly grants them.

`Query` reads retained scripted progress for the requested session. For active keyed vanilla objectives,
credit progress is the current nonnegative credit balance capped at the required amount, not cumulative
earnings; travel progress is 0 or 1 from vanilla completion (a visit timestamp threshold, not dwell time).
Vanilla retains those resource/visit facts with the save; the API does not copy them into a competing
progress counter. Queries re-resolve the held mission and verify its shape, never write resources or
visit history, and do not return stale pre-load progress. Offered or retired non-scripted objectives
have no live progress answer. Unavailable state has no numeric progress; it is not reported as zero.
Live writes resolve the current player and held mission each time, so callers never keep a native
objective reference across reloads.

`WithRevision(newRevision, migratesFromRevision)` explicitly permits migration from one retained
revision. Supported migrations are fully keyed scripted definitions: all old keys and required amounts
must remain, steps may reorder, and added keys start at zero. Missing keys, changed kinds/amounts,
unapproved revisions and insufficient retained-state capacity are refused without discarding state.
Existing mixed or unkeyed objectives do not acquire invented migration identities. Authored narrative
logic stays in the consumer; the API stores objective state, not campaign-specific flags.

Controlled in-game verification covers independently loaded authored/generated consumers sharing local
objective names, partial-progress reload, stale sessions, inactive steps, scripted revision reordering,
native payout/idempotency, repeated instances, older-save rollback and restored terminal state.
Host regressions additionally cover reentrancy and migration quota boundaries. Non-scripted progress
queries have host and installed-shape checks plus controlled in-game snapshot comparison and reload:
answers match current credits/native travel completion without changing resources or serialized mission
state. This does not cover a spending sequence or a native visit transition. Full acceptance remains
incomplete; this coverage does not make the entire API runtime-qualified.

## Automatic persistence

The module registers the reserved persistence owner `vgmodapi.story-content` (schema 4) with its own
capture/restore/validate. The payload is a bounded `VSC1` binary record set written with netstandard
binary IO and STRICT UTF-8 only (invalid bytes and unpaired surrogates are refused on both read and
write, never decoded to replacement characters) — no JSON library is introduced. The occurrence
sequence is the authoritative timeline and must be positive, unique and strictly increasing on both
sides. Payloads are capped well below the 1 MiB
envelope bound; truncated, extended, malformed or newer-version payloads are refused.

Schemas 1–3 are readable through registered owner migrations. Schema 1 has no pending choices
or observed-failure flag; schemas 1 and 2 have no objective layouts. Schema 3 has no retained definition
payload. Such legacy occurrences still require matching startup definitions for reconstruction.
Capture writes schema 4 without modifying older snapshots. Newly offered occurrences retain their
immutable definition data: text, faction, steps, native targets, rewards and declared choice keys.
Startup registration still establishes provider ownership and behavior, but same-revision definitions
cannot replace saved generated data. Explicit supported revision migration replaces the snapshot
transactionally. Active revision migration requires unchanged non-step metadata, including rewards
and choice declarations; legacy active occurrences without a retained definition cannot prove that
condition and refuse revision migration. Offered occurrences may migrate the full definition.
Definition bytes consume existing quotas and are discarded at retirement; outcomes,
choices and keyed progress remain governed by retention policy. Cold-start restoration has host checks and bounded two-process native evidence: offered and active
occurrences retain their target and required amount despite changed startup definitions, then survive
reload and normal retirement. This uses the same canonical save directory; cross-directory migration
and native verification of every retained metadata field are not covered. Keyed layouts retain objective positions, kinds, required amounts,
scripted progress and content revision. Their space is charged before admission or migration.
Host regressions cover coordinator migration at the provider/global limits, service restoration,
completion and older-snapshot rollback. Controlled in-game verification covers scripted objective revision migration; arbitrary non-scripted
migration is not supported.

The codec is canonical: the encoder and the decoder run the SAME ledger bounds (per-provider quota,
per-provider payload budget including reservations, per-definition campaign cap over retired AND
unresolved occurrences, choices only on terminal records, temporary horizon,
sequence range, payload size, identity uniqueness, retired-implies-exactly-one-outcome),
so a payload can never restore a ledger the ledger's own operations would refuse, and the ledger can
never reach a state its own capture would refuse. A payload that violates a bound is refused, which
blocks that owner and protects its retained bytes; nothing is silently pruned to fit.

The existing coordinator rules apply unchanged: state is restored only for a generation matching the
exact loaded save, absence means no known state, and corrupt/unsupported data blocks that owner
instead of restoring empty state. Restoring an older generation REPLACES the ledger, so a newer
snapshot's completion cannot leak into an older loaded save. When persistence is unavailable or
paused, offering new content is refused with a diagnostic rather than silently accepting a
persistent mission that would not be saved.

Unregistering a definition stops offering new content. A registration handle releases only the
registration it made: one from a released lease, or one whose identifier has since been registered
again — even with the same immutable definition object — does nothing, so a stale handle can never
remove live content. It never rewrites or deletes saved
occurrences: removal of persisted references follows `content-safety.md`, and provider-required
content still needs its provider.

## The native slice: installing into the game, and what the game decides

The module requires `Story/Enabled` (default off), inspected bindings, API-managed saves,
native protection and observed mission transitions. `ModApi.Services.Story` is a stable
`IStoryService`; inspect `Availability` or observe `AvailabilityChanged` for binding/protection
health. An unavailable instance does not register a replacement save provider. Session
restore, suspension and provider mutation readiness remain separate; their diagnostics do not
change the installed binding fact. Observed
transitions are required, not optional: without them a completion could never be recorded, and the
only alternative would be letting a caller declare one.

| Moment | What the API does natively |
|---|---|
| Registration | Installs the definition into the game's story catalog under its base identifier, and resolves the definition's SOURCE FACTION against the game's own faction registry. An unknown faction refuses registration. |
| Collision | The game's own registration REPLACES a duplicate identifier, so the adapter checks first and refuses (`IdentifierInUse`) instead. Nothing existing is ever overwritten, and a refused installation rolls back the API-side registration with it. |
| `Offer` | Installs a catalog entry for THAT OCCURRENCE, under its own identifier, and refuses a travel objective aimed at a point of interest this world does not have, which could never be completed. |
| Travel targets | Re-checked immediately before the game is asked to accept, because a world can lose a place in between; a restored occurrence whose destination is gone is NOT vouched for, so the guards quarantine it rather than run a mission that could never finish. Its record and its bytes are untouched, and it is vouched for again once the world has the place. |
| Generator | Builds a `Mission` through the game's own objective and reward factories, from the supported subset's fields only, and sets the context every vanilla generator sets. No consumer delegate is captured and none is ever persisted. |
| `Activate` | Asks the game's own `IsMissionsLimitExceeded` first, because the route this API uses (`AddMissionWithLog`) does not, and refuses when accepting would leave more missions or objectives than the protection guard can scan. Then asks the game to accept the mission (`force:false`, so its own duplicate-story refusal applies) and VERIFIES the player holds exactly that mission. A refusal leaves the occurrence offered and the world untouched. |
| `Retire(Abandoned/Failed)` | Removes the mission from the world first (`completed:false`, so nothing is archived as finished) and records the outcome only once the world no longer holds it. |
| `Retire(Completed)` | Refused. A completion is recorded from the OBSERVED completion in the game (see below). |
| Release | Disposing a registration, a lease or the module uninstalls only the catalog entries this API installed, and only while the catalog still holds our own entry. An occurrence the player is still holding keeps its entry. |

Faction identities are the game's own: it resolves `Source.Galaxy.Factions.<identifier>` as a TYPE,
so the identities are those PascalCase type names (`TradingGuild`, not `tradingGuild`). Its lookup
never returns null — it constructs and registers whatever the name resolves to, and throws otherwise —
so the API resolves the type itself before asking, and an unknown identity is refused without ever
touching the game's faction registry.

### Why a mission needs a source faction

The game writes `sourceFaction.identifier` unconditionally when it saves a held mission, and reads it
back through its own faction registry. A mission without one makes the player's save throw, which is
why `StoryMissionDefinition` requires a `StoryFactionId` and why the adapter also sets the source
location (the player's current point of interest, which the game tolerates being absent) and the
dynamic level flag every vanilla generator sets. For the same reason the `KillEnemies` objective kind
is REFUSED at registration: it serializes an enemy faction's identifier, counts kills against that
faction and renders its name, and this subset has no owner-scoped faction identity for objectives.
The kind stays in the vocabulary and is refused with that reason rather than installed unsafely.

### One catalog entry per occurrence

The game archives a completed story identifier and its duplicate check consults that archive, so a
single shared identifier could be accepted exactly ONCE per save. Each occurrence therefore gets its
own catalog entry, `vgmodapi.story.<provider>.<local>.<occurrence>`, derived deterministically from
the content identity and the occurrence so a reload reinstalls exactly the same entries without
storing the string. A repeated occurrence of a completed definition is accepted normally.

### Outcomes come from the game

A completion is never declared by a caller. The module watches the same mission boundary consumers
see and records the outcome the game produced for an owned occurrence:

- an observed completion or failure records that outcome, once;
- an observed abandonment records an abandonment;
- a NEUTRAL removal says nothing about why the mission ended, so the occurrence stays unresolved
  rather than being called complete or failed.

Choices belong with a completion the caller does not perform, so they are declared while the
occurrence is live with `DeclareChoices`, validated exactly as a retirement validates them, and
written when the game ends it. A completion can arrive in a later session, so a declaration is part
of the occurrence's PERSISTED state, inside the space its outcome already reserved: it
survives a reload, it is transferred into the record rather than kept beside it, and it belongs to
the save it was made in — rolling back to a generation from before the declaration restores an
occurrence with no declaration, not one carrying a newer session's choices.

An observed FAILURE is not a terminal outcome. The game leaves a failed story mission in the player's
list and offers to retry it, so a reported failure is recorded as a fact about a live occurrence: it
stays active and keeps its catalog entry, and it is settled by what follows.

The retry button is one operation on ONE occurrence: the game removes the mission and re-adds the
same identifier. The game removes BY REFERENCE and re-adds with its duplicate check forced off, so
the route is allowed only for an object the player ACTUALLY holds, whose identifier is held exactly
once; a stale object carrying an admitted identifier would remove nothing and add a second live
mission for it. The guard wraps the whole method and tells the module, which suspends the outcome the
removal would otherwise record and holds the catalog entry.

While that is open it holds the SAME boundary as this module's own native operations, in BOTH
directions: no other mutation interleaves with it — every public call is refused as `Busy`, including
one for the occurrence being abandoned — and the button itself cannot open while one of this module's
own native operations is running, which is exactly what a consumer observer inside an acceptance
would otherwise do. No catalog entry is released underneath it, including one a provider teardown
asked for, and registration is refused for as long as it is open: a teardown queued during the
boundary removes an identifier by NAME later, so re-registering the same local ID meanwhile would
hand that queued removal a brand new entry to delete. That is what keeps the game's own re-add from
looking up an entry this module just removed.

Each opening mints its own transaction token, bound to the session that opened it. A finalizer can
arrive after a synchronous callback replaced the session — the same save, the same occurrence
identity, a different world — and that token is what makes it harmless: it is checked BEFORE anything
is inspected, settled or reported, so a stale finalizer reads nothing, degrades nothing, settles
nothing and does not close a transaction the new session opened. The original's exception is still
passed on unchanged.

Afterwards, what the game turned out to be holding settles it, and the cases are distinguished:

- a NEW mission for that identifier is a retry: the same occurrence continues and its reported
  failure is cleared, because only a re-acceptance by the game clears one;
- the ORIGINAL object still being there means nothing happened: it is not a retry, so the reported
  failure stands and no outcome is recorded;
- nothing held is the ending it looked like: a reported failure becomes final, anything else is the
  abandonment the player asked for;
- anything else — the world could not be read, or holds that identifier more than once — decides
  NOTHING. The occurrence, its reported failure, its declared choices and its catalog entry are all
  preserved and the module stops for the session rather than inventing an ending.

A mission carrying a follow-up identifier is refused rather than redirected, because re-adding it
would install something this module never admitted. `Retire` remains for the outcomes a caller genuinely owns — an
abandonment or a failure it decides — and those end the mission in the game first.

Everything session-scoped ends with the session: catalog entries of that save's occurrences are
uninstalled, and pending declarations, in-flight bookkeeping, suspensions and faults are cleared. A
suspension or a fault is for THAT session, exactly as this document says; provider registrations are
not, because they belong to the process.

### Transactions across the native boundary

Accepting or ending a mission dispatches the game's own mission observers synchronously, so consumer
code runs INSIDE those calls. Everything is therefore validated before the world is touched — lease,
session, availability, ownership, state, outcome, choice bounds and encodability — and re-validated
afterwards. A story mutation attempted from inside such a call is refused as `Busy`, and a catalog
removal a disposal asks for waits until the operation completes.

Only STABLE facts are re-checked after the native call — the lease, the module's own state, the
session — never transient ones. A save starting or callbacks dispatching inside a consumer's observer
cannot make a change the game already made wrong, so they never roll back a good acceptance or block
the module.

If an acceptance can no longer be recorded (the consumer disposed its lease, the save reloaded), the
API undoes exactly the mission that acceptance produced, without archiving it. If that undo fails, or
if an outcome cannot be recorded after the mission was already ended in the game, the module BLOCKS
itself for the session and says so: a mission already ended cannot be un-ended without replaying its
acceptance side effects, so nothing further is built on a world the module cannot account for.

### Quarantine: an owned mission nobody vouches for cannot advance or pay out

The game saves accepted missions as full objects, so an API-owned mission comes back whether or not
the module that owns it is present, enabled, bound or willing. Refusing API calls does nothing for
it: the game would keep updating it, completing it and paying its rewards. So the protection is
NATIVE and separate from the module:

- It is bound under its own capability, `story-protection` (`Story/Protection`, default ON), whenever
  the inspected assembly matches — independently of `Story/Enabled` and of whether the story module
  binds at all, because an orphan is dangerous exactly then.
- It guards every way such a mission can advance or pay out: the per-frame `Mission.Update` (which is
  also the auto-complete route), `GamePlayer.CompleteMission`, `Mission.ClaimRewards`,
  `Mission.MissionFailed`, and `MissionObjective.ProcessMissionTrigger`, the one virtual method every
  objective the supported subset can install goes through. Binding REFUSES if an installable objective
  kind overrode that method, rather than guarding it incompletely.
- It guards the game's own abandon/retry BUTTON, which is `MissionDetails.AbandonMission`: that
  method removes the mission and, for a retryable story mission, re-adds `nextMissionOnFailed ??
  storyId` out of the catalog — a lookup that throws when an orphan's entry is gone. The whole method
  is wrapped, so for an orphan nothing of it runs: the mission stays in the player's list and the
  catalog is never asked. (`Mission.RetryAsNextMission` is also guarded, but it is a private
  follow-up-only route that API content never takes; it is not the button.)
- Objective ownership is resolved against the CURRENT player on every call, not cached against
  admissions, so a second save loaded into the same process is seen even with the story module off.
  The scan is bounded; if it cannot be completed the objective is REFUSED and the capability reports
  itself degraded until the next session, rather than letting possibly owned content through.
- It fails CLOSED over the whole reserved namespace. Only the exact occurrence identifiers the owning
  module vouches for, in the session it vouched for them, run; every other identifier beginning with
  `vgmodapi.story.` — a base definition id, a malformed one, an occurrence this module never minted —
  is quarantined. The match is ordinal, so a neighbouring namespace such as `vgmodapi.story-other.`
  is not affected. With nothing admitted — module absent, disabled, unbound, suspended, faulted,
  disposed, or a session that has not restored — every identifier this API could have written is
  quarantined. Nothing else is ever affected: a vanilla story id, another mod's
  mission or a mission with no identifier is not ours to judge.
- It changes NOTHING about the content it refuses. The mission stays in the player's list exactly as
  it was loaded, its objectives and flags are untouched, and it projects byte-for-byte as before, so a
  provider that returns later finds its content intact. (Host tests compare a projection of the
  persisted shape, including the failure, abandon, tracking, idle, auto-complete, follow-up and step
  visibility fields; running the game's own serializer is PR3 work.)
- Its blast radius when it CANNOT decide is deliberately wider than that. Ownership is answered from
  the player's own missions; if that cannot be read, or holds more than the guard can scan, an
  objective trigger is refused even though the objective may belong to the game or another mod, and
  the capability reports itself degraded. Refusing a vanilla trigger for a moment is recoverable;
  letting an orphan pay itself out is not. While degraded, no owned content is registered, offered or
  accepted, every admission is withdrawn, and recovery needs a NEW session in which an ownership scan
  actually completes — not merely clearing the message.

The module refuses to install any content at all when the guards are unavailable: owning content that
a later session could not protect is worse than owning none.

**Where the protection does not reach.** Two states leave owned content in an EXISTING save
unguarded, and this API cannot fix either from inside the game:

- an uninspected assembly (a game update), where every hook including this one stays off; and
- `Story/Protection=false`, which is a deliberate opt-out.

In both, a save that already contains owned missions loads and those missions run and pay out
normally. The API refuses to install new content and reports the capability unavailable, but it
cannot refuse the load, halt the game, or mark the world unsupported: no mod can do that safely, and
pretending otherwise would be a false guarantee. The honest guidance is: with protection unavailable,
do not load a save that contains owned story content, and keep the backup this project's checks
already recommend. Every protection claim in this document holds ONLY for the inspected assembly with
protection enabled and bound.

### Load: the game restores its own missions, and orphans suspend the module

The game saves the player's held missions as full OBJECTS (`missions` written from `Mission.ToJson`)
and only resolves the story catalog for a string element, which its own saves never write. So a
missing provider does NOT stop a load and is NOT protected by the game: the mission would simply come
back orphaned, with live objectives and rewards. That is what this policy exists for.

When the module's own state is restored it reinstalls the catalog entry of every unresolved
occurrence and correlates the ledger with the world by occurrence identifier:

- An active occurrence whose mission the world no longer holds is REPORTED, including whether the
  world archived it, and left exactly as recorded. Nothing is deleted and no outcome is invented,
  because the archive alone cannot say whether the story was completed or abandoned.
- A world that cannot be inspected at all suspends the module too: an orphan cannot be ruled out, so
  nothing is run over a world this module has not actually examined.
- An owned identifier live in the world that no admitted occurrence claims, or an unresolved
  occurrence whose provider is not registered, SUSPENDS the module for the session: every mutation is
  refused, the reason is reported for a provider-required compatibility state, the native mission is
  left exactly where the save has it, and not one persisted byte is rewritten. Nothing is adopted,
  substituted or deleted, and no placeholder generator is ever installed. Suspension also withdraws
  every admission, so the native guards stop the very content the suspension is about; the capability
  is reported as UNAVAILABLE while it lasts.

### Difficulty mapping

The game's scale is `Easy, Normal, Hard, Skull, Insane` plus non-scalar tiers, so this API's fourth
tier maps onto `Skull` rather than inventing a name. The mapping is pinned against the installed
assembly, including that the mapped names exist and stay in ascending order; an unmapped tier refuses
installation instead of guessing.

## Boundary and status

Delivered here: public contracts, authenticated provider leases, session-scoped availability and
session-scoped mutations, unresolved-occurrence discovery, identity policy, registry, occurrence
ledger with its retention policy, bounded codec, automatic persistence registration, the native
install/accept/abandon adapter with per-occurrence catalog entries and rollback, observed outcomes
from the game's own mission boundary, the orphan suspension policy, the `ModApi.Services.Story` surface behind
the inspected-assembly gate, host tests against the production adapter and installed-assembly pins.
Two independently loaded example authors exercise a generated job and a hand-authored campaign
through the same API without provider save/load hooks. Controlled native probes cover ownership,
offered/active restoration, outcomes, save refusal, rollback, repeated instances, provider absence,
scripted objective progress and revision migration. A separate process verifies retained definitions
at the same canonical save path despite changed startup data. Host and codec tests cover additional
malformed-input, quota and schema boundaries. These are bounded checks, not exhaustive native
coverage; `RuntimeQualified` remains false. Narrative choreography, extra custom mechanics and voice
synthesis remain provider responsibilities.

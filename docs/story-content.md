# Owned story content — foundation (#13, part 1 of the delivery)

This document describes the API-owned story contract delivered so far: the public types, the
owner-scoped identity model, the explicitly supported mission subset and the automatic persistence
of API-owned story state. **It does not yet install anything into the game.** Registration handles
record what must be installed; driving vanilla registration, reconstruction and mission acceptance
is separate work, and no runtime qualification is claimed. `#13` stays open; this foundation is not
its acceptance.

## The contract in one paragraph

A provider registers an immutable `StoryMissionDefinition` under a `StoryContentId`
(`provider` + `localId`). The API derives the namespaced identifier the game will store, refuses
collisions, mints a separate identity for every occurrence, retains outcomes according to the
definition's retention policy, and captures/restores that state through its OWN persistence
provider. A provider writes no codec, no save/load callback and no restoration scheduling for this
state. Additional provider-owned information keeps using the separate save-data API.

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

`StoryContentId` segments are 1–48 lowercase ASCII letters/digits/hyphens starting with a letter —
never a path, alias or display name. The identifier is `vgmodapi.story.<provider>.<local>`, so two
independently loaded mods that both register `mission-x` produce different identifiers and neither
can capture the other's saved content. Refusals are fail-closed and diagnosed:

| Status | Meaning |
|---|---|
| `DuplicateLocalId` | The same provider registered that local ID twice in this process |
| `IdentifierInUse` | The identifier already exists in this world; the API never replaces it |
| `LimitExceeded` | The bounded registry (256 definitions) is full; nothing is dropped |
| `Unavailable` | The module is disposed or the capability is absent |

## Occurrences, retention and limits

Every offered occurrence receives its own identity. Only the owning `StoryContentId` may activate or
retire it; a foreign provider is refused without revealing the owner's local ID. A terminal outcome
is recorded exactly once — a second attempt is refused rather than rewriting an authoritative
result. Retention is declared per definition:

- `Temporary`: keeps offered/active state plus a bounded idempotency tombstone (outcome only). No
  declared choices, no completed payload, no history.
- `Campaign`: additionally retains queryable authoritative outcomes and supported declared choices
  (at most 16 per occurrence). `IsCompleted` answers campaign completion **without MissionJournal**.

Bounds are refusals, never truncation: at most 2048 occurrences and 64 retained outcomes per
definition. Exceeding one diagnoses and changes nothing, so campaign progression is never silently
dropped. Storing an outcome or a declared choice value is not narrative history; mod-specific
decisions stay mod logic.

## Automatic persistence

The module registers the reserved persistence owner `vgmodapi.story-content` (schema 1) with its own
capture/restore/validate. The payload is a bounded `VSC1` binary record set written with netstandard
binary IO and UTF-8 only — no JSON library is introduced. Payloads are capped well below the 1 MiB
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

Delivered here: public contracts, identity policy, registry, occurrence ledger, bounded codec,
automatic persistence registration, host tests and installed-assembly pins. **Not** delivered here:
installing definitions into `StoryMission`, reconstructing offered/active content in a live session,
driving acceptance/outcome transitions from observed native boundaries, the two-consumer
demonstration and the native qualification pilot. Those remain required for #13, and
`RuntimeQualified` stays false.

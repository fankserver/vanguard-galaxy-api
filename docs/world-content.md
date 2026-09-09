# Owned world content boundary

World protection is opt-in and experimental. `ModApi.World` exposes an experimental declaration facade only when protection installs successfully. Native creation returns `Unavailable`, and owned-definition load admission remains disabled. Host checks and binding inspection do not qualify persistent world creation in Unity.

## Declaration facade

Acquire `IWorldProvider` directly from the loaded plugin assembly and register immutable `WorldCombatSiteDefinition` values before starting a session. Definitions identify local content, revision, display name, an existing faction ID and level. Same-owner duplicate declarations are rejected; registration does not create a POI. The authenticated lease owns its declarations and must be disposed on provider teardown.

`CreatePersistentCombatSite` specifies an expected session, declaration ID, separate instance GUID, existing system ID and coordinates. Its implementation is behind the closed runtime-admission gate. A `WorldSiteReference` carries provider/local/instance identity only: it is not a native object or permission to mutate another owner's content. `FindPersistentCombatSite` checks the authenticated provider, expected session and exact current native membership; a reference alone does not establish existence. Lookup is also behind the closed runtime-admission gate. Native qualification, migration and dependent-reference delivery remain prerequisites to supporting these operations.

## Shared primitive selection

Two consumers motivate an owned encounter location, not a campaign framework:

- Anima's `MissionFactoryFromJson.BuildClearCombatSite` creates a Combat POI, attaches a mission objective and composes guards/reinforcement waves. Its fleet helpers request count ranges within point-budgeted generation.
- CustomMission's `SectorBuilder` builds pocket systems and gated entrances, chooses faction ships, and uses fixed payloads for exact-count timed spawns. Its scoped-hostility helper explicitly avoids changing global faction diplomacy.

The selected implementation boundary is a persistent, independently identified Combat POI in an existing system. Pocket-system and gate authoring are not implied by that selection. Six-act progression, boss escape, dungeon layouts, faction politics, autopilot rules and combat choreography remain consumer logic. A capability used by only one campaign detail is not sufficient justification for a shared API.

### Spawn semantics

Exact-count and point-budgeted operations must be distinct contracts:

- Native fixed payload creation constructs the requested number of one selected ship type.
- Native budgeted creation spends a total points budget and can return fewer units than a requested count. Its maximum may also expand from parent level and faction ship-budget calculations. Setting input count bounds is not an effective generation bound.
- Scoped per-unit hostility is not a change to the faction's global relationship with the player.

No public spawn operation is exposed. `UnitPayloadDescriptor` is refused by world inspection until effective generation bounds are validated. Fixed-descriptor input bounds and native selector checks are not complete validation of generated content.

## Declarative revision migration

Registration may supply one exact previous declaration. Supported migration advances the revision and may rename the site; local identity, faction and level must be unchanged. Matching only a previous revision number is insufficient: the complete retained declaration must match. During verified reconstruction, the API updates a native name only if it still equals the old declared default, preserving customized names and mutable instance level/state. The next automatic save records the current declaration/revision. Instance and mission-target identities do not change. Undeclared or incompatible older definitions remain refused; this is not arbitrary provider serialization or a general migration callback.

## Owned actor lifetime guards

Spawned units and persistable roots retain their originating manager/session; later activity does not borrow a replacement manager. Rejected captures remain classified. Unit initialization, damage, collision callbacks and selected coroutine continuations are guarded, and persistable updater writes require the original spawn-data reference. Known roots are rechecked during fixed updates, provider release, session changes and teardown. Refused live roots have their own rigidbody simulation and colliders disabled without traversing unrelated child actors.

These paths remain unqualified in Unity. Independently spawned equipment/projectiles, other persistable subtype behavior and physics scheduling still require coverage; disabling root physics is not a complete scene quarantine or safe-uninstall guarantee.

## Persistence and activation constraints

Supported persistent creation must automatically preserve existence, owner/local identity, instance identity, supported properties, links and lifecycle state. Providers must not implement save hooks or rebuild timing for those fields. Temporary lifetime must be explicit; it must not replace persistence merely to avoid reconstruction.

The internal implementation pairs world inventory and full declarations in one committed save generation, associates them with the exact native snapshot and uses an API-required save-version envelope. Reconstruction requires the exact admitted constructor results, not just matching IDs. Two providers may declare the same local ID without sharing ownership; same-owner duplicate declarations are rejected.

Activation requires exact reconstruction or creation provenance and readiness of both automatic persistence owners. Load-time provider absence refuses before construction. No hot-unload guarantee is offered; retained refusal guards require process restart. Keeping immutable data after a provider lease is disposed is not, by itself, evidence that native activity is safe.

Before admission can be enabled, nested state must satisfy the supported data-only contract at creation, save and load. Native type membership and a committed digest do not establish behavioral safety: triggered descriptors execute generation during loading, getters can generate deferred content, and scene/travel coroutines resume after their factories return. Executable provider payloads and custom native types/state cannot be admitted under a data-only lifetime contract.

Successful creation/lookup results carry a `PoiId` for `StoryObjective.TravelTo`. Reserved world targets are checked through the world's restored inventory and exact native membership, not the vanilla lookup alone. Story provider segments resolve to authenticated host plugin IDs; references to another world's owner are refused. These read-only checks work during `PlayerReady` after world reconstruction, before dependent story/bar restoration, while public creation remains blocked during dispatch. Missing or unready world dependency inspection is unknown, never proof of existence.

Mission references must resolve only after world reconstruction, with dependent story and bar restoration ordered accordingly. Native qualification must cover save/load, cross-slot changes, save-as/rollback, failed saves, missing providers, content migration and reference restoration, without duplicates or unrelated-world changes. The internal guards are not evidence that these end-to-end requirements are complete.

# Development contract and scope

The [roadmap](https://github.com/fankserver/vanguard-galaxy-api/issues/1) tracks future work, acceptance criteria and dependencies. This document defines the implementation constraints; it is not a second backlog or a completion report.

## Current surface

VGModAPI provides core session/load/save observation, default-enabled experimental mod save data, optional experimental mission and travel/station observation, optional experimental owned story content, and a local mod-information catalog with a main-menu interface. Availability depends on each service's configuration and prerequisites. See the [README](../README.md) for consumption and configuration, and [compatibility](compatibility.md) for qualification limits.

Owned story content supports a closed mission subset with definition registration, native catalog installation, occurrence identity, reconstruction and automatic persistence. It is not a general scripted-objective or campaign framework. Content/schema migration and objective integration remain partial under [#13](https://github.com/fankserver/vanguard-galaxy-api/issues/13); the scripted-objective API tracked by [#14](https://github.com/fankserver/vanguard-galaxy-api/issues/14) is not implemented. See [the story contract](story-content.md) for supported behavior.

## Automatic content persistence

Supported persistent API-owned content must save and reconstruct automatically. Mod authors register definitions/behavior and declare lifetime/retention; they must not supply save hooks, codecs, sidecars or manual restoration scheduling for API-owned fields. The generic save-data API is for **additional custom mod data**, not mandatory glue for API content.

- Identity is stable provider/plugin ID plus local ID, never mutable display name or an author-invented string prefix. Two providers may define the same local ID; repeated live instances remain distinct from definitions. Namespaces prevent accidental collisions, not malicious same-process access.
- Persistent creation defaults to saved content. Required persistence being unavailable must refuse creation or report unavailability, not silently create unsaved content. Explicitly transient effects, UI handles and observations need no permanent record.
- Temporary missions retain necessary offered/active progress. Campaign missions retain authoritative completion/outcomes and supported choices within that save, without requiring a journal plugin. Optional narrative history is separate. Retention limits must fail safely rather than truncate progression.
- Reload, new game, save-as, slot changes, rollback, failed/skipped writes, provider absence and supported migrations must preserve ownership and references. Reuse safe vanilla serialization where appropriate; do not promise cross-file atomicity or serialization of executable custom behavior.

These requirements apply to each content module's supported scope. Observer/storage plumbing alone does not satisfy them. Consumer integrations retaining their own save hooks are not evidence that the API manages those consumers' content.

Boarding-specific source mappings and applicable contract constraints are documented in [boarding integration](boarding-contract.md). It distinguishes optional experimental boarding observation from unavailable commands/content and unqualified native behavior.

## Integration boundaries

- Inspect the original installed game implementation before changing semantic hooks. Signature matching alone is insufficient. Reject uninspected hashes until reinspection and qualification.
- Expose verified state transitions, not renamed Harmony method-call notifications. A coroutine factory returning is not completion; `GameplayInitialized` is not universal scene/POI/UI readiness; `SaveGame.Store` returning is not proof of a successful write.
- A runtime session ID is not a persistent campaign ID. Reject stale signals and references after session replacement.
- Recursive save retries belong to one logical operation. Success, failure and skip remain distinct; successful vanilla saving does not imply atomic mod-sidecar persistence.
- Keep public contracts in `VGModAPI.Abstractions` free of vanilla and Unity types. Core state machines and reflection adapters are internal implementation details. Harmony hooks belong in `VGModAPI/Patches`.
- Consumers compile against abstractions; the API installation supplies the runtime assembly. BepInEx remains the plugin loader and dependency manager.
- Keep combat rules, campaign choreography and other bespoke gameplay in feature mods. Direct Harmony use outside supported API coverage is explicitly version-sensitive.

## Validation and delivery

Use the [project checks](checks.md), including pure state-machine/adapter tests, installed binding checks and package validation. Cover nesting/reentrancy, stale sessions, retries, skips, subscriber disposal and individual observer failures. Test doubles do not simulate Unity scheduling; metadata checks do not execute Harmony.

Controlled native testing requires explicit authorization and disposable/copied saves. Record tested boundaries and remaining gaps without treating host tests or a bounded native probe as full in-game acceptance. `RuntimeQualified` remains false. Never deliberately damage real saves or redistribute game references, decompiled source, raw profiles or private fixtures.

Source and documentation describe current behavior, supported compatibility and constraints only. Development chronology and superseded decisions do not belong in the repository. Retained superseded public APIs must be explicitly deprecated with replacement guidance. Follow [contributor guidance](../CLAUDE.md) for branches, reviews and delivery permissions.

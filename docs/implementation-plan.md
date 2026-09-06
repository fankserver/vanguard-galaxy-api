# Implementation plan

## Current roadmap and mandatory developer contract

The live [roadmap #1](https://github.com/fankserver/vanguard-galaxy-api/issues/1) and its seven milestones govern delivery. The initial-scope sections below are a historical first-milestone plan, not the current scope or completion report. Observer/storage plumbing does not complete the content modules.

Owner clarification: all supported persistent content supplied by the API must save and reconstruct automatically. Mod authors register definitions/behavior and declare lifetime/retention; they must not supply save hooks, codecs, sidecars or manual restoration scheduling for API-owned fields. The generic save-data API is for **additional custom mod data**, not mandatory glue for API content.

- Identity is stable provider/plugin ID plus local ID, never mutable display name or author-invented string prefixes. Two providers may both define `MissionX` or `PoiX`; repeated live instances are distinct from definitions. Namespaces prevent accidental collisions, not malicious same-process access.
- Persistent creation defaults to saved content; unavailable required persistence must refuse or report unavailability, not silently create unsaved content. Explicitly transient effects, UI handles and observations need no permanent record.
- Temporary missions retain necessary offered/active progress; campaign missions retain authoritative completion/outcomes and supported choices within that save, without MissionJournal. Optional narrative journal history is separate. Retention limits must fail safely rather than truncate progression.
- Reload, new games, save-as, slot changes, older-save rollback, failed/skipped writes, provider absence and schema/content migration must preserve ownership and references. Reuse safe vanilla serialization where appropriate; do not promise cross-file atomicity or serialization of executable custom behavior.

Delivery owners: #13 missions, #14 objectives, #15 patrons, #19 items, #20 recipes/jobs, #21 persistent POIs/world content; #24 requires no-save-boilerplate mission/POI examples with two providers sharing local IDs. #22/#23/#25 preserve this contract through compatibility, game updates and future adapter changes. #11/#12 remain observation prerequisites. Anima's #11 observer migration explicitly retains legacy v4 save/load/factory hooks and does **not** fulfill #13/#14; its remaining supported-state persistence must move into those content APIs later. Completed #7–#10 are supporting infrastructure/policy, not proof of managed-content delivery.

## Historical initial status and decisions

The owner selected **plan for approval** and **core lifecycle first**, then authorized implementation. The core is now implemented with passing automated checks; in-game qualification remains pending. See `lifecycle-contract.md` for the implemented contract and `compatibility.md` for evidence.

Name: VGModAPI. Repository: `vanguard-galaxy-api/`. First experimental version: `0.1.0`.

Implementation refinements:

- Added an internal `VGModAPI.Core` assembly so state machines and the reflection adapter can be tested without Unity.
- Narrowed proposed `GameplayReady` to `GameplayInitialized`: verified gameplay-manager initialization, not universal scene/POI readiness. `world-ready` remains explicitly unavailable.
- Implemented assembly-hash gating, 42 pure/adapter tests, 7 installed-metadata checks, and a compiled observer example.
- No deployment, existing-mod migration, or general persistence/story/UI services yet.

Work directly without subagents. Do not migrate existing mods, deploy, publish, or perform destructive save tests as part of initial implementation without further authorization.

## Objective

Provide dependable answers to three questions:

1. Which runtime session is active?
2. What has actually finished loading?
3. Did a logical vanilla save operation succeed, fail, or get skipped?

The API must expose verified semantic boundaries, not rename Harmony prefixes/postfixes as gameplay events.

## Initial scope

- Session identity and invalidation.
- Load/new-game phases, player readiness, gameplay readiness, and failure reporting where observable.
- Logical save-operation tracking across retries.
- Isolated, disposable subscriptions.
- Capability/compatibility reporting.
- Pure state-machine tests and installed-assembly binding checks.

Excluded: mission events, story/objective registration, general persistence framework, sidecar rewriting, save-format changes, UI helpers, content registration, combat/autopilot rules, and automatic consumer migration.

## Phase 1: verify installed-game boundaries

Freshly inspect the original installed game DLL; checked-in sibling decompilation is not current enough to trust blindly.

Trace:

- Save-file loading, nested coroutines, rejected future-version saves, and load failures.
- New-game creation and return to menu.
- Player assignment versus asynchronous gameplay/scene initialization.
- Interrupted or superseded session starts.
- Save writes, metadata writes, ephemeral-player skips, recursive retries, and final failure.

Deliver `docs/lifecycle-contract.md`: each event's exact meaning, supported paths, ordering, readiness conditions, and vanilla implementation boundary.

Deliver `docs/compatibility.md`: tested assembly identity, target bindings, unsupported paths, and qualification evidence. Record an assembly hash and game version when verifiable; do not infer version from old documentation.

If a promised boundary cannot be observed reliably, narrow the contract or mark the capability unavailable. Do not infer success from a method returning or a coroutine merely being created.

## Phase 2: public contracts and integration split

Proposed structure:

```text
VGModAPI.sln
Makefile
README.md
CLAUDE.md
.gitignore
docs/
VGModAPI.Abstractions/
VGModAPI/
  Plugin.cs
  Lifecycle/
  Compatibility/
  Patches/
VGModAPI.Tests/
```

- Runtime: BepInEx 5, HarmonyX, `netstandard2.1`.
- No runtime dependencies on existing feature mods.
- Abstractions: immutable payloads, session/operation identities, subscription handles, capability status, and queryable current state.
- Keep vanilla and Unity types out of the public lifecycle contract.
- Runtime owns all vanilla references and patches.
- Consumers compile against abstractions; API distribution supplies the runtime copy. Define discovery and dependency instructions before shipping an example consumer.
- Do not duplicate BepInEx's plugin loader or dependency manager.

## Phase 3: session lifecycle

Provisional state model:

```text
No session -> Loading/Starting -> Player ready -> Gameplay ready
                         -> Failed
Active session -> Invalidated
```

Provisional notifications (names are not frozen):

| Notification | Contract |
|---|---|
| SessionInvalidated | Previous session references must no longer be used |
| SessionStarting | A tracked load or new-game attempt began |
| PlayerReady | Current player reconstruction/creation completed |
| GameplayReady | Verified gameplay initialization completed |
| SessionStartFailed | A tracked start attempt failed |

Every start attempt receives an identity. Reject delayed signals belonging to superseded attempts. Define handling of repeated signals, menu transitions, partial initialization, and interruptions.

A runtime session ID is not a persistent campaign ID. Do not invent campaign identity through filenames or process-global state.

## Phase 4: save operations

Track Started, Succeeded, Skipped, and Failed as distinct outcomes.

Requirements:

- Recursive retries belong to one logical operation.
- Exactly one terminal outcome per tracked operation when its outcome is observable.
- Ephemeral-player early return is Skipped, not Succeeded.
- Returning from `SaveGame.Store` alone is not success.
- Include operation identity, destination, and session identity where known.
- Specify whether success includes metadata completion and how partial failures are represented.
- Successful vanilla saving does not imply atomic mod-sidecar persistence.

Do not alter the vanilla save format or add sidecar writes in this milestone.

## Phase 5: subscription and capability safety

- Explicitly disposable subscriptions with owner identity for diagnostics.
- Dispatch independently to each subscriber; one exception must not suppress later subscribers.
- Document callback thread, ordering, reentrancy, and subscription changes during delivery.
- Support querying state for late subscribers; define whether registration itself replays anything.
- Clean unsubscription and unpatching on shutdown.
- Expose per-capability availability and reasons, rather than treating plugin startup as proof that hooks work.
- Avoid automatically disabling consumers solely for throwing callbacks.
- Fail locally where safe, but do not advertise dependent capabilities when prerequisites are unavailable.

## Validation gates

### Automated

Test without constructing Unity MonoBehaviours:

- Load success/failure, new game, menu return.
- Replacement during pending initialization and stale callbacks.
- Repeated signals and event ordering.
- Save success/failure/skip/retry aggregation.
- Throwing subscribers, disposal, and reentrant subscription changes.

Check patch targets against the installed original assembly. Binding checks demonstrate signature compatibility, not semantic correctness or live Unity behavior.

### In-game qualification

Using disposable test saves:

1. Load in space and docked.
2. Start a new game.
3. Switch saves without restarting.
4. Return to menu and reload.
5. Exercise manual saves and autosave rotation.
6. Exercise controlled failures and skipped writes where feasible.

Do not damage or deliberately corrupt the owner's real saves. Arrange live qualification separately if the environment cannot run the game. Until completed, label the result **implemented but not runtime-qualified**.

## Build and distribution

- Prefer `make build` and `make test`.
- Resolve compile references locally; never commit game, Unity, or BepInEx DLLs.
- Package only required API assemblies/dependencies, not broad output-directory DLL globs.
- Do not assume the game supplies Newtonsoft.Json: workspace migration notes report its removal. Initial core should avoid introducing a serializer without need.
- Document install, dependency declarations, compatibility limits, and a minimal lifecycle consumer.
- No deployment or existing-mod migrations in the initial implementation.

## Follow-up roadmap

Future work is now maintained in the [pinned GitHub roadmap](https://github.com/fankserver/vanguard-galaxy-api/issues/1), with [milestones](https://github.com/fankserver/vanguard-galaxy-api/milestones), acceptance criteria, evidence, priority labels, and native blocked-by relationships. Update those issues rather than maintaining a second detailed backlog here.

This document preserves the initial implementation plan; `lifecycle-contract.md` and `compatibility.md` describe the implemented surface and evidence. Milestone placement does not authorize deployments or consumer migrations, and no release dates are promised.

Keep gameplay choreography in feature mods and direct Harmony access available as an explicitly version-sensitive escape hatch.

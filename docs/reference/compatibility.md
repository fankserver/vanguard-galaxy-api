# Compatibility and qualification

## Current status

**Experimental, with bounded controlled native evidence; full in-game acceptance remains pending.** `RuntimeQualified` is false. Controlled probes exercise specific core, persistence, mission/travel, story and menu paths. They do not qualify every configuration, consumer or source revision. Rebuilding a candidate or passing host tests does not establish native coverage for that candidate.

The adapter accepts only this inspected original `Assembly-CSharp.dll` SHA-256:

```text
a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb
```

The controlled environment reports **game 0.8.2.3, Unity 6000.4.7f1, BepInEx 5.4.23.5**. The hash identifies the accepted assembly; version labels or a matching hash alone do not prove live compatibility.

An uninspected hash leaves the API available for diagnostics but disables game integration. The local mod-information catalog does not require native binding. Do not simply update the hash: reinspect semantics, update mappings/tests and qualify the new build. Injected hash rejection or a changed PE overlay tests refusal, not compatibility with an alternate game implementation.

## Available surface and limits

| Surface | Current behavior | Qualification boundary |
|---|---|---|
| Core lifecycle/save outcomes | Session replacement, load/new-game attribution, player readiness, gameplay-manager initialization and logical save outcomes | Controlled load/save paths are exercised; no universal POI/UI readiness or arbitrary asynchronous callback guarantee |
| Mod save data | Default-enabled experimental storage of additional custom mod payloads | MissionJournal/Stockpile pilots exercise bounded roundtrip, refusal/retry, import and teardown paths; no cross-file atomicity or exhaustive crash-recovery claim |
| Boarding observation | Optional default-off inspected target/operation snapshots and scoped events | Host and installed-binding tests only; no native boarding qualification, command or authored-content capability |
| Recipes and jobs | Optional default-off Forge/refining definitions, producer lookup, issued-station advisory quotes and scoped job/transfer observations | Host/reflection and installed-binding tests only; no Unity recipe acceptance or mutation capability |
| Mission observation | Optional experimental transition and identity services | Only documented hooks and identity-continuity paths are supported; observation is not automatic content persistence |
| Travel/station observation | Optional experimental native observers | Controlled routes and consumers are exercised; tutorial rewrite, latent inherited dispatch and recovery miss-cleanup limits remain explicit in the travel contract |
| Owned story content | Optional, default-off experimental registration, catalog installation, occurrence reconstruction and automatic persistence of a closed subset | Bounded native story, new-game and absent-author paths are exercised; content/schema migration and objective integration remain partial, and host migration tests are not native migration qualification |
| Story load protection | Default-on guard on the inspected build, independent of story-author registration | With the guard disabled or the game uninspected, the API cannot refuse unsafe owned-story loads; do not load those saves in that state |
| Mod information | Process-local catalog and default-on native main-menu entry when binding succeeds | Bounded menu interactions are exercised; presentation acceptance, physical gamepad behavior and browser opening are not fully qualified. No automatic update-check service is provided |

Story remains incomplete; the general scripted-objective API is not implemented. Supported payload-schema compatibility is separate from migration of arbitrary authored definitions or scripted objectives.

## Controlled coverage

This summary describes the scope of available controlled evidence, not a claim that the current checkout was run in Unity. Exact candidate identities, receipts and detailed execution reports belong outside this source tree. Verify their applicability before asserting a candidate is qualified.

Core probes cover:

- Docked, mining-space, native in-system-transit and controlled parked-empty-space loads; session replacement, return to menu and reload.
- Tutorial creation through native wizard callbacks, with player readiness after synchronous configuration. This is not general pointer-driven new-game UI acceptance.
- Valid-syntax newer-header rejection without readiness, corrupt-JSON failure and an equal empty-player current-header control. Public events do not themselves identify a version-rejection cause.
- Pending-player replacement detection without claiming full arena startup.
- Unity-driven stale adapter readiness/failure signals through a real observed iterator and unfinished disposal. Explicit synthetic adapter signals are not vanilla load events or arbitrary asynchronous engine callback coverage.
- Manual and quit saving, autosave rotation, ephemeral-player skips, exhausted recursive retries and recovery after a transient write failure.
- Individual throwing subscribers and disposal, plus bounded MissionJournal/Stockpile coexistence and missing/unavailable API refusal.

Additional controlled probes cover documented mission/travel consumer paths, owned-story reconstruction and absent-author handling, and menu input/lifecycle interactions. Consult [mission](mission-events.md), [travel](travel-events.md), [story](story-content.md), [mod information](mod-information.md) and [runner instructions](https://github.com/fankserver/vanguard-galaxy-api/blob/main/docs/development/qualification-runner.md) for their supported cases and exclusions. A phase's evidence must not be extended to unrelated phases or consumers.

## Verification layers

- **Pure host tests** exercise state machines, coroutine observation, reflection adapters, storage rules and package tooling. Small doubles do not simulate Unity scheduling.
- **Installed binding checks** inspect original game metadata through Mono.Cecil without executing game code. They establish declared shapes, not live Harmony behavior.
- **Synthetic Windows checks** exercise sandbox file isolation, cleanup and typed preference snapshot/restore against synthetic data; they do not launch Unity.
- **Controlled native probes** run narrowly specified scenarios with copied saves and isolated preferences. Their assertions and case prerequisites bound what they demonstrate.
- **Full in-game acceptance** remains separate, including unexercised configurations, presentation, broader input behavior and remaining content integration.

No fixed test count or historical PASS table substitutes for checking the candidate being delivered.

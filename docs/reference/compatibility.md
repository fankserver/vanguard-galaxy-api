# Compatibility and supported behavior

## Supported game build

The API is experimental. Back up saves before changing plugins; do not assume
universal safe uninstall or compatibility with arbitrary other mods.

The adapter accepts this inspected original `Assembly-CSharp.dll` SHA-256:

```text
a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb
```

The corresponding environment is game **0.8.2.3**, Unity **6000.4.7f1**, BepInEx
**5.4.23.5**. Unrecognized assemblies leave diagnostics available but disable game
integration. Do not bypass the hash gate or update it without checking the affected
bindings and behavior. The local mod catalog does not require game binding.

Service references are stable and non-null. Their typed `Availability` reports
binding/dependency health, not session readiness or permission to mutate game state.
Check the service's current context and each operation's result. Automatic module
initialization does not remove compatibility or save-safety guards.

## API and consumer versions

API **0.2.0** uses `ModApi.Services`. Consumers must compile against that surface;
0.1.x nullable globals and interfaces are not provided. Pre-1.0 minor versions may
break source and binary compatibility. There is no stable ABI promise or automatic
adaptation of old consumers.

| Consumer and installation | Expected result |
|---|---|
| Consumer compiled against the current API | Uses its declared typed contracts; operations still enforce current availability and context |
| Required consumer with API absent or below its declared minimum | BepInEx refuses the missing/incompatible dependency |
| Consumer compiled against removed 0.1.x members | Unsupported; type/member resolution can fail even if a minimum-version attribute passes |
| Optional consumer with API absent | Its loader boundary must avoid resolving API types; a soft-dependency attribute alone is insufficient |
| Disabled service or unsupported game assembly | Stable service reference with explicit unavailable diagnostics; operations refuse |

Declare the required API minimum. BepInEx remains the only loader; its minimum
check does not prove compatibility with every newer API. Distribute one API copy,
not embedded runtime copies in each consumer. Public contracts contain no Unity or
game types, except for supported Unity UI types in the optional `VGModAPI.Unity`
bridge; Abstractions and Core remain Unity-free. Core and native adapters are
unsupported implementation details.
Callbacks and provider namespaces are not a security sandbox: mods share a process.

## Save and feature boundaries

API/package versions, payload schemas, provider identities and game-binding health
are independent. An API update does not authorize discarding saved data. Integrity
checks, supported schema migrations and refusal/preservation of unknown payloads
remain required. Provider/local IDs are durable keys, not display names; renaming
a plugin does not automatically move its records to another namespace.

Lifecycle events do not imply universal world/UI readiness. A native save outcome
is not a cross-file transaction. Observation services do not create persistent
content. API-owned content saves its supported state automatically; additional
custom mod data remains separate. Story's supported subset is not a general
scripted-objective or campaign API.

See the domain contracts for supported operations and actual limitations:
[lifecycle](lifecycle-contract.md), [save data](persistence-storage.md),
[missions](mission-events.md), [travel](travel-events.md), [story](story-content.md),
[bars](bar-rosters.md), [boarding](boarding-contract.md), [dungeons](dungeon-content.md),
[crafting](recipes.md), [gameplay UI](gameplay-ui.md) and [mod information](mod-information.md).

## Correctness checks

Focused tests exercise the public contracts, service/state behavior, callbacks,
ownership, save/load safety and error/refusal paths. Installed-binding tests check
reflected game members without executing the game. Package checks exclude runtime
reference assemblies and unsafe archive contents. Commands are in the [Makefile](https://github.com/fankserver/vanguard-galaxy-api/blob/main/Makefile).
These checks support feature delivery; they do not promise universal correctness or
authorize deployment. There is no separate qualification or native-acceptance gate.

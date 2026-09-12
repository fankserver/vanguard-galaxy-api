# Author integration

Compile against `VGModAPI.Abstractions`, mark the reference non-copy-local, and declare a BepInEx dependency on the minimum API version your mod uses. Install one API package; do not bundle Core or a private Abstractions copy in every mod. `ModApi.Services` supplies stable typed services. Availability is not permission to mutate: use current session/target handles and inspect each result.

## Compiling examples

`make build` compiles the API and every example project. Examples are not deployed or included in the API package. BepInEx references belong in thin bootstrap projects; reusable game logic uses public abstractions. Consumer-owned UI may reference Unity and the optional `VGModAPI.Unity` bridge.

Each example is one self-contained package: its own folder, its own project, and a README describing
what it demonstrates. See the [examples index](../../examples/README.md).

| Example | Demonstrated behavior |
|---|---|
| PocketWorlds | Pocket systems, wormhole pairs, placement, named subsectors, quiet traffic, static names, resource and combat sites, and full cleanup |
| CargoRecovery | Authored boarding content plus contextual panel actions, command leases, tactical requests and observed settlement |
| StoryMissions | Authored campaign and generated job, mixed objective kinds, declared choices, and a follow-up offered from a completion reaction |
| StationCommerce | Owned trade goods, recipe-first owned dependencies, story-linked bar contacts, roster ownership, and two independent providers sharing local IDs |
| UiSurfaces | Gameplay UI lifecycle with an owned Unity container, plus Unity-free Forge inspection and HUD actions |
| Observation | Observed lifecycle, mission and travel facts, optional-dependency entry points and additional custom counter save data |
| UpdateParticipant | Optional update metadata integration |

Authors register definitions in **`Start()`**, not `Awake()`, and create occurrences only from explicit gameplay logic with an existing system ID. They do not create sites automatically on every load. BepInEx populates `Chainloader.PluginInfos[].Instance` only *after* a plugin's `Awake()` returns, and instance-authenticated providers (world, story, bars, items, recipes) resolve the caller against exactly that entry — acquiring one in `Awake()` returns null and silently registers nothing. Independently loaded projects have different authenticated plugin IDs, so sharing a local ID or occurrence key cannot bind one owner's occurrence to the other. Persisted native identity, not a retained Unity object, determines restoration.

Supported owned story/world/item/recipe/dungeon data is saved by the API. Do not add provider save hooks, codecs or sidecar writers for those fields. `Observation/Consumers/CustomCounter.cs` deliberately shows a different concern: **additional custom mod information**, for which the generic save-data API is appropriate. See each domain contract for supported shapes, missing-provider handling and migrations; arbitrary custom gameplay state is not inferred from a registration.

## Integration boundaries and future official support

| Boundary | Current responsibility | What a real official interface could replace |
|---|---|---|
| Public contracts | Semantic identities, immutable snapshots, operations, ownership and refusal results | Retain when semantics match; explicitly deprecate/replace where they do not |
| Native adapter | Inspected private members, Harmony interception, native construction and save/load bridges | Replace individual hooks or data access only after inspecting actual official behavior |
| Loader/bootstrap | BepInEx discovery, dependencies and Unity plugin lifetime | Requires explicit host/consumer migration if the official loader differs |
| Feature-mod logic | Narrative, balance, UI choices and unsupported mechanics | Remains mod-owned; not automatically converted by changing an adapter |
| Managed save data | Provider namespaces, retained per-save outcomes and automatic restoration of supported content | Must remain API-managed or have an explicit compatible migration; never silently return serializer work to every author |

There is no speculative second backend, replacement loader or universal interception facade. Future official support does not imply automatic migration, loader-independent discovery or safe uninstall. Retain/deprecate/replace decisions require concrete interfaces and saved-data compatibility analysis, not a promise based on a roadmap.

A mod may keep direct Harmony patches for uncovered mechanics. Isolate those patches and their game references from API-backed logic, keep ownership of their compatibility checks, and avoid overlapping an API-owned interception path. Using a few services does not make the entire mod game-independent.

## Game updates

The API can centralize compatible native member changes, UI adapter changes and owned-data reconstruction. It cannot repair Doorstop/BepInEx loader failure, invent removed serializers or automatically translate arbitrary consumer patches. A loader/runtime change may require updated BepInEx and consumer bootstrap assemblies.

Only the inspected hash in [compatibility](compatibility.md) is accepted today. Compilation alone never adds a hash. Supporting another build requires explicit member/behavior checks, per-capability support decisions and save-format compatibility; do not silently map multiple builds to one adapter. Existing unsupported-build refusal remains active until that work is done.

Use the project's compile/package checks to keep runtime references out of packages. The adapter binds the game's inspected LightJson types; it does not assume Newtonsoft is bundled by the game. A consumer needing a separate serializer must handle its own runtime dependency and Unity/Mono compatibility rather than copying Unity/System facade DLLs.

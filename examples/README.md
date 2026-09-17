# VG Mod API examples

Each folder is **one self-contained package**: its own project, its own README, and a coherent slice
of what the API can do. Every package builds from its own folder and is a real, runnable mod (except
where noted), not a snippet.

None of these are part of the shipped API package.

## The packages

| Package | What you can build with it | Services it exercises |
|---|---|---|
| **[PocketWorlds](PocketWorlds/)** | **Your own star systems** — pocket systems chained together, hidden off the drawn map, reachable only through wormholes you placed, holding your own mining fields, salvage wrecks and hostile encounters, and removable without a trace. | `World` |
| **[CargoRecovery](CargoRecovery/)** | **Your own boarding missions** — a derelict you spawn in your own system, laid out room by room, with an authored decision in the cargo hold, extraction through a contextual panel action, crew settlement observed honestly, and clean removal afterwards. | `Dungeons`, `World` |
| **[StoryMissions](StoryMissions/)** | **Your own missions** — hand-authored campaign beats with real decisions, jobs generated from runtime text, and follow-ups that offer themselves when an arc completes. | `Story`, `Game` |
| **[StationCommerce](StationCommerce/)** | **Your own trade goods and the people who deal in them** — a manufactured item, the recipe that makes it, and a bar contact with a mission, placed at a real station. | `Items`, `RecipeRegistration`, `Bars`, `Story`, `Game` |
| **[UiSurfaces](UiSurfaces/)** | **UI, settings and saved progress** — a hosted window with four typed global preferences, a per-save counter, and a Forge inspector. Requires the matching development API, not public 0.2.8. | `GameplayUi`, `Hud`, `Settings`, `SaveData`, `ForgeUi`, `Recipes`, `RecipeQuotes` |
| **[TractorGuide](TractorGuide/)** | **Tractor policy and tooltip text without patches** — a settings-gated extra autopilot beam, a mastery hint on tractor module stats, an Engineering badge line and an opt-in item tip. Requires the matching development API, not public 0.2.8. | `Equipment`, `SkillTrees`, `Tooltips` |
| **[Observation](Observation/)** | **Mods that react to what the player is doing** — sessions, missions and travel observed without touching them, plus optional-dependency entry points and custom save data. | `Lifecycle`, `Missions`, `Travel`, `SaveData` |
| **[UpdateParticipant](UpdateParticipant/)** | **Publishing your mod** — update metadata and the packaging dry run. | *(loader metadata only)* |

## Build

```bash
make build-examples                 # every package, including nested variant/consumer projects
dotnet build examples/PocketWorlds/PocketWorlds.csproj   # or just one
```

Building needs your local game/BepInEx references (`make link-libs`). Deploy a package's DLL into
`BepInEx/plugins/` alongside the separately installed API; never deploy a duplicate
`VGModAPI.Abstractions`.

## Two conventions worth knowing

**Acquire instance-authenticated providers in `Start()`, not `Awake()`.** BepInEx populates
`Chainloader.PluginInfos[].Instance` only *after* a plugin's `Awake()` returns. Providers such as
world, story, bars, items, recipes and settings authenticate against that entry; acquiring them in
`Awake()` is refused. Plugin-id-keyed registrations have different timing: HUD registration can
happen in `Awake()`, and `SaveData.Register` must happen before a gameplay session starts.
UiSurfaces therefore registers its save data and HUD in `Awake()`, and acquires settings in `Start()`.

**The API owns persistence for API-owned content.** Definitions are declared once and the API
restores their missions, progress and choices without consumer save hooks or sidecar writers.
`Observation/Consumers/CustomCounter.cs` and `UiSurfaces/WindowVisits.cs` instead persist
*additional custom mod data* through `SaveData`. UiSurfaces' global preferences remain in BepInEx
config; they are not copied into per-save data.

## Packages with a second project

Two packages build more than one assembly, on purpose:

- `StationCommerce/AuthorB/` — the same `Plugin.cs` compiled again with `AUTHOR_B`, so two
  independently loaded authors can be seen registering identical local IDs without colliding.
- `Observation/Consumers/` — plain .NET, with **no BepInEx or Unity reference**. It stays separate
  because the host tests load it into an isolated context that refuses `VGModAPI.Abstractions`
  resolution, proving its optional-dependency entry point exposes no API type. These classes are
  not deployable plugins; a plugin injects them.

`CargoRecovery/CargoEncounter.cs`, `CargoRecovery/CargoEncounterPanel.cs` and
`UiSurfaces/Inspector.cs` are likewise Unity-free and are compiled by the host tests without BepInEx.
Lift them into a non-Unity assembly if you want that split — you do not need a separate host project.

## Safety

Use disposable saves when trying example content. Do not install examples into an existing campaign
without explicit intent and backups.

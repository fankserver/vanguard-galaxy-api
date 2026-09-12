# VG Mod API examples

Each folder is **one self-contained package**: its own project, its own README, and a coherent slice
of what the API can do. Every package builds from its own folder and is a real, runnable mod (except
where noted), not a snippet.

None of these are part of the shipped API package.

## The packages

| Package | What you can build with it | Services it exercises |
|---|---|---|
| **[PocketWorlds](PocketWorlds/)** | **Your own star systems** — pocket systems chained together, hidden off the drawn map, reachable only through wormholes you placed, holding your own mining fields, salvage wrecks and hostile encounters, and removable without a trace. | `World` |
| **[CargoRecovery](CargoRecovery/)** | **Your own boarding missions** — a derelict laid out room by room, an authored decision in the cargo hold, extraction requested through a contextual panel action, and crew settlement observed honestly. | `Dungeons`, `DungeonPanel`, `DungeonCommands`, `DungeonTactics`, `DungeonOperations`, `DungeonSettlement` |
| **[StoryMissions](StoryMissions/)** | **Your own missions** — hand-authored campaign beats with real decisions, jobs generated from runtime text, and follow-ups that offer themselves when an arc completes. | `Story`, `Game` |
| **[StationCommerce](StationCommerce/)** | **Your own trade goods and the people who deal in them** — a manufactured item, the recipe that makes it, and a bar contact with a mission, placed at a real station. | `Items`, `RecipeRegistration`, `Bars`, `Story`, `Game` |
| **[UiSurfaces](UiSurfaces/)** | **Your own interface inside the game** — a Unity window owned entirely by your mod, plus a Forge inspector showing requirements and output previews. | `GameplayUi`, `Hud`, `ForgeUi`, `Recipes`, `RecipeQuotes` |
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

**Acquire providers in `Start()`, never `Awake()`.** BepInEx populates
`Chainloader.PluginInfos[].Instance` only *after* a plugin's `Awake()` returns, and the
instance-authenticated providers (world, story, bars, items, recipes) resolve the caller against
exactly that entry. Acquiring one in `Awake()` returns null and silently registers nothing. Dungeon,
HUD and panel registrations use a plain plugin-id string instead, but every example here follows the
same rule so nothing depends on which identity a given service happens to use.

**Keep runtime strings ASCII.** The game's `pixel16` font has no glyph for `—`, `→` or `…`; TextMeshPro replaces each one with a space and logs a warning per render, so a fancy dash in a HUD row quietly becomes a gap plus log noise. Use `-`, `->` and `...` in anything the game displays. Prose in comments and READMEs is unaffected.

**The API owns persistence.** No example registers a save hook, codec or sidecar writer for
API-owned content; definitions are declared once and the API restores occurrences, progress and
choices. The one deliberate exception is `Observation/Consumers/CustomCounter.cs`, which persists
*additional custom mod data* — a different concern, and the correct use of the generic save-data API.

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

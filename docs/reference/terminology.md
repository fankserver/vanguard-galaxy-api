# Game terminology and compatibility

Use native/game terminology for the corresponding concept, without exporting native types. A public snapshot/handle suffix distinguishes copied data or permission from a live Unity object; it does not define a different gameplay system.

| Game concept | Public API terminology / distinction |
|---|---|
| GamePlayer, save/load, gameplay initialization | Lifecycle sessions and save outcomes; a session is an API runtime attempt, not a campaign or universal world-ready state |
| Mission, StoryMission, objectives | Missions and Story; retained outcomes are gameplay state, not optional journal history |
| Bar, patrons, dialogue lines | Bars and Dialogue; speech claims do not replace native narrative |
| Map POI, system, gates, Combat | Station/Navigation and World Combat-site declarations; a site reference identifies one occurrence, not a new POI type |
| Inventory, cargo, global armory, material storage | Inventories with explicit owner/location/kind; station material storage is player-owned, not shop stock |
| InventoryItemType, CraftingRecipe, Forge, Refinery | Items, RecipeRegistration, Recipes, CraftingJobs/Commands and ForgeUi; registration and observation remain distinct |
| Ship boarding / becoming boardable | BoardingRules and ship-target distinctions; do not call every dungeon a ship capture |
| DungeonManager / DungeonOperation | DungeonOperations and DungeonCommands; includes approach/crew transport as well as simulation |
| DungeonSimulation / DungeonOptions | DungeonTactics and DungeonCombat; both ship interiors and walk-in installations use the dungeon simulation |
| DungeonDefinition, DungeonPanel and settlement | Dungeons, DungeonPanel, DungeonRewards and DungeonSettlement; authored content, presentation and actual delivery are separate |
| HUD and mod menu | Hud and Mods; plugin identity differs from display name and installed version |

## Migration from published boarding names

Starting with API 0.2.6, prefer these accessors:

| Published accessor | Canonical accessor / interface |
|---|---|
| `ModApi.Services.Boarding` | `DungeonOperations` / `IDungeonOperationService` |
| `ModApi.Services.BoardingCommands` | `DungeonCommands` / `IDungeonCommandService` |
| `ModApi.Services.BoardingTactics` | `DungeonTactics` / `IDungeonTacticalService` |
| `ModApi.Services.BoardingCombat` | `DungeonCombat` / `IDungeonCombatService` |

The old accessors are deprecated, not removed. They return the **same instances**, with the same subscriptions, capability state, handles and ownership checks. Existing binaries retain their original method signatures. New interfaces inherit the published contracts; `BoardingHandle`, `BoardingOperationSnapshot`, `BoardingCommandOptions` and related DTO names remain valid compatibility types. They represent native dungeon operations/options, not competing runtime models. Do not duplicate DTO graphs or convert live handles merely to change spelling.

Update accessor names and minimum dependency version when recompiling. Existing injected consumers accepting `IBoardingService` or the other published interfaces still accept the canonical service. No save, provider ID, configuration key, capability key or native enum value is renamed. This is a source-guided migration, not permission to reinterpret persisted data.

`BoardingRules` is retained because it also governs becoming boardable and ship-integrity boundaries before a dungeon operation exists. Its shared simulation settings keep published types for compatibility. New APIs for shared interior mechanics should use DungeonSimulation/DungeonOperation vocabulary; retain *boarding* where the actual mechanic is ship boarding.

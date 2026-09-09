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

## Dungeon service names

Dungeon operation, command, tactical and combat access uses one canonical set of accessors: `DungeonOperations` (`IDungeonOperationService`), `DungeonCommands` (`IDungeonCommandService`), `DungeonTactics` (`IDungeonTacticalService`) and `DungeonCombat` (`IDungeonCombatService`). `BoardingHandle`, `BoardingOperationSnapshot`, `BoardingCommandOptions` and related DTO names describe native dungeon operations/options for ship boarding and installations; they are shared data contracts, not a competing runtime model. No save, provider ID, configuration key, capability key or native enum value encodes a service accessor name.

`BoardingRules` keeps the *boarding* name because it governs becoming boardable and ship-integrity boundaries before a dungeon operation exists. New APIs for shared interior mechanics use DungeonSimulation/DungeonOperation vocabulary; retain *boarding* where the actual mechanic is ship boarding.

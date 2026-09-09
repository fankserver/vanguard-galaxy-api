namespace VGModAPI;

/// <summary>Observation of native DungeonOperation lifetimes for both boardable ships and dungeon locations.
/// Inherits the published snapshot contracts to preserve consumer binary compatibility.</summary>
public interface IDungeonOperationService : IBoardingService { }
/// <summary>Commands corresponding to DungeonManager/DungeonOperation, including crew transport.</summary>
public interface IDungeonCommandService : IBoardingCommandService { }
/// <summary>Movement and directives within DungeonSimulation.</summary>
public interface IDungeonTacticalService : IBoardingTacticalService { }
/// <summary>Combat policy within DungeonSimulation, for ships and installations.</summary>
public interface IDungeonCombatService : IBoardingCombatService { }

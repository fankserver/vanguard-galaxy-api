using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class DungeonAegisBindings
{
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        ("Source.Data.Persistable.DungeonLocationData", "stationIsInvincible", "System.Boolean", false, true),
        ("Source.Data.Persistable.DungeonLocationData", "dungeonData", "Source.Dungeon.DungeonData", false, true),
        ("Source.Data.Persistable.DungeonLocationData", "stationData", "Source.Data.Persistable.CombatStationData", false, true),
        ("Source.Dungeon.DungeonData", "isOperationActive", "System.Boolean", false, true),
        ("Source.Dungeon.DungeonData", "dockingDestroyed", "System.Boolean", false, true),
        ("Source.Dungeon.DungeonData", "facilityIntegrity", "System.Single", false, true),
        ("Source.Dungeon.DungeonData", "simulation", "Source.Dungeon.DungeonSimulation", false, true),
        ("Source.Dungeon.DungeonSimulation", "structuralCollapse", "System.Boolean", false, false),
        ("Source.Dungeon.DungeonSimulation", "isRetreating", "System.Boolean", false, false),
        ("Source.Data.Persistable.CombatStationData", "stationParts", "System.Collections.Generic.IEnumerable`1<Source.Data.CombatStationPartData>", false, false),
        ("Behaviour.Unit.CombatStationPart", "dungeonLocationData", "Source.Data.Persistable.DungeonLocationData", false, true),
        ("Source.Galaxy.MapPointOfInterest", "current", "Source.Galaxy.MapPointOfInterest", true, false)
    };
    internal static void Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        // Constructed accessors whose shapes RecipeCatalogBindings cannot express are proven by
        // constructing the runtime itself, which throws before installation on any mismatch.
    }
}

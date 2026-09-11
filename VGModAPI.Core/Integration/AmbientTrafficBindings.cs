using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class AmbientTrafficBindings
{
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        ("Behaviour.Managers.BasePoiManager", "poi", "Source.Galaxy.MapPointOfInterest", false, false),
        ("Source.Galaxy.MapElement", "guid", "System.String", false, false),
        ("Source.Galaxy.MapElement", "system", "Source.Galaxy.SystemMapData", false, true),
        ("Source.Galaxy.GalaxyMapData", "current", "Source.Galaxy.GalaxyMapData", true, false),
        ("Source.Galaxy.GalaxyMapData", "allPointsOfInterest", "System.Collections.Generic.IEnumerable`1<Source.Galaxy.MapPointOfInterest>", false, false),
        ("Source.Galaxy.GalaxyMapData", "allSystems", "System.Collections.Generic.IEnumerable`1<Source.Galaxy.SystemMapData>", false, false)
    };
    /// <summary>Periodic decorative "passerby" spawners only; docking, services and payload delivery are untouched.</summary>
    internal static readonly MethodBinding[] Methods =
    {
        new("trafficStationSpawn", "SpacestationExteriorManager", "CreatePasserbyShip", false, "System.Boolean", "Behaviour.Spacestation.Docking.DockingOptionSize"),
        new("trafficGateSpawn", "Behaviour.Travel.JumpGateManager", "CreatePasserbyShip", false, "System.Void"),
        new("trafficWormholeSpawn", "Behaviour.Travel.WormholeManager", "CreatePasserbyShip", false, "System.Void"),
        new("trafficSecurityPatrol", "Behaviour.Managers.BasePoiManager", "CreateSecurityPatrol", false, "System.Void")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}

using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Inspected authored-pocket-system creation/gate boundaries. Declaring bindings does not install a world capability.</summary>
internal static class PocketSystemBindings
{
    internal const string Sandbox = "Source.Simulation.World.SandboxWorld";
    internal const string System = "Source.Galaxy.SystemMapData";
    internal const string Sector = "Source.Galaxy.SectorMapData";
    internal const string Element = "Source.Galaxy.MapElement";
    internal const string Poi = "Source.Galaxy.MapPointOfInterest";
    internal const string JumpGate = "Source.Galaxy.POI.JumpGate";
    internal const string Player = "Source.Player.GamePlayer";
    internal const string Galaxy = "Source.Galaxy.GalaxyMapData";
    internal const string Faction = "Source.Galaxy.Faction";
    internal const string SectorNameGenerator = "Source.Galaxy.NameGenerator.Sector";
    internal const string Vector2 = "UnityEngine.Vector2";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (System, "pocketSystem", "System.Boolean", false, true),
        (System, "sector", Sector, false, true),
        (Element, "position", "UnityEngine.Vector2", false, true),
        (System, "storyteller", "Source.Simulation.World.SystemStoryteller", false, true),
        (Poi, "hidden", "System.Boolean", false, true),
        (JumpGate, "jumpgateOpen", "System.Boolean", false, true),
        (JumpGate, "targetSystemGuid", "System.String", false, true),
        (JumpGate, "targetPoiGuid", "System.String", false, true),
        (Element, "guid", "System.String", false, false),
        (Element, "system", System, false, true),
        (Sector, "systems", "System.Collections.Generic.List`1<" + System + ">", false, true),
        (Galaxy, "sectors", "System.Collections.Generic.List`1<" + Sector + ">", false, true),
        (Player, "currentSystem", System, false, true),
        (Player, "currentPointOfInterest", Poi, false, true),
        (Player, "waypoints", "System.Collections.Generic.List`1<" + Poi + ">", false, true),
        (Element, "level", "System.Int32", false, true),
        (Element, "faction", Faction, false, false),
        (Element, "name", "System.String", false, false)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("authoredEntrance", System, "GetEntranceJumpgate", false, JumpGate),
        new("gateTarget", JumpGate, "GetTargetPOI", false, Poi),
        new("gateUnlock", JumpGate, "UnlockJumpgate", false, "System.Void"),
        new("gateLock", JumpGate, "LockGate", false, "System.Void"),
        new("systemRemovePoi", System, "RemovePointOfInterest", false, "System.Void", Poi),
        // OffMap placement: allocate a distant free position with the game's own seeded allocator, create a
        // new sector there, and place the pocket system in it (wormhole-only door, off the settled map).
        new("galaxyRandomPosition", Galaxy, "GetRandomPosition", true, Vector2,
            "System.Collections.Generic.List`1<" + Vector2 + ">", "System.Single", "System.Single", "System.Single", "System.Single", "System.Single"),
        new("galaxyAddSector", Galaxy, "AddSector", false, "System.Void", Sector),
        new("sectorCreate", Sandbox, "CreateSector", true, Sector, Vector2, "System.String"),
        new("sectorName", SectorNameGenerator, "GenerateSubsectorName", true, "System.String"),
        new("emptyCreate", Sandbox, "CreateEmptySystem", true, System, Sector, "System.Int32", Faction, Vector2, "System.Boolean"),
        new("gatePair", Sandbox, "CreateJumpgatePoi", true, "System.Void", System, System, "System.Boolean", "System.Boolean")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}

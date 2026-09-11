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
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (System, "pocketSystem", "System.Boolean", false, true),
        (System, "sector", Sector, false, true),
        (System, "storyteller", "Source.Simulation.World.SystemStoryteller", false, true),
        (Poi, "hidden", "System.Boolean", false, true),
        (JumpGate, "jumpgateOpen", "System.Boolean", false, true),
        (JumpGate, "targetSystemGuid", "System.String", false, true),
        (JumpGate, "targetPoiGuid", "System.String", false, true),
        (Element, "guid", "System.String", false, false),
        (Element, "system", System, false, true),
        (Sector, "systems", "System.Collections.Generic.List`1<" + System + ">", false, true),
        (Player, "currentSystem", System, false, true),
        (Player, "currentPointOfInterest", Poi, false, true),
        (Player, "waypoints", "System.Collections.Generic.List`1<" + Poi + ">", false, true)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("authoredCreate", Sandbox, "AddSideContentSystemToSystem", true, System, Sector, System, "System.Int32"),
        new("authoredEntrance", System, "GetEntranceJumpgate", false, JumpGate),
        new("gateTarget", JumpGate, "GetTargetPOI", false, Poi),
        new("gateUnlock", JumpGate, "UnlockJumpgate", false, "System.Void"),
        new("gateLock", JumpGate, "LockGate", false, "System.Void"),
        new("systemRemovePoi", System, "RemovePointOfInterest", false, "System.Void", Poi)
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}

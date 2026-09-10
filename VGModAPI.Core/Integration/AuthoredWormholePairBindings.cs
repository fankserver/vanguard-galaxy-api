using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class AuthoredWormholePairBindings
{
    internal const string System = "Source.Galaxy.SystemMapData";
    internal const string Element = "Source.Galaxy.MapElement";
    internal const string Poi = "Source.Galaxy.MapPointOfInterest";
    internal const string Wormhole = "Source.Galaxy.POI.Wormhole";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (System, "pointsOfInterest", "System.Collections.Generic.List`1<" + Poi + ">", false, true),
        (Element, "guid", "System.String", false, false),
        (Element, "system", System, false, true),
        (Element, "name", "System.String", false, false),
        (Poi, "hidden", "System.Boolean", false, true),
        (Wormhole, "discovered", "System.Boolean", false, true),
        (Wormhole, "targetWormholeGuids", "System.Collections.Generic.List`1<System.String>", false, true)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("wormholeSetup", System, "SetupPOI", false, Element, Element, "System.Nullable`1<UnityEngine.Vector2>", "Source.Galaxy.Faction", "System.Int32"),
        new("wormholePosition", System, "GetRandomPosition", false, "UnityEngine.Vector2", "System.Single", "System.Single", "System.Single", "System.Single")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}

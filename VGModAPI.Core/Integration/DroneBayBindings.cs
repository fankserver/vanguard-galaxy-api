using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal static class DroneBayBindings
{
    internal const string Bay = "Behaviour.Equipment.Module.DroneBayModule";
    internal static readonly (string Type, string Member, string Shape, bool Static, bool Field)[] Members =
    {
        (Bay, "_droneAmount", "System.Int32", false, true),
        (Bay, "droneBonusAmount", "System.Int32", false, true),
        (Bay, "shouldDeploy", "System.Boolean", false, true),
        (Bay, "drones", "System.Collections.Generic.List`1<Behaviour.Unit.Drone>", false, true),
        (Bay, "transitionDuration", "System.Single", false, false),
        ("Behaviour.Equipment.AbstractEquipment", "parent", "Behaviour.Unit.AbstractUnit", false, false)
    };
    internal static readonly MethodBinding[] Methods =
    {
        new("droneLaunchDuration", Bay, "get_transitionDuration", false, "System.Single"),
        new("droneReplacementRoll", Bay, "GetDronePrefab", false, "Behaviour.Unit.Drone", "System.Int32", "Source.Data.AbstractUnitData"),
        new("droneAdd", Bay, "AddNewDrone", false, "System.Void", "System.Int32"),
        new("droneCatalog", "Behaviour.Unit.Drone", "Get", true, "Behaviour.Unit.Drone", "System.String")
    };
    internal static Dictionary<string, MethodInfo> Validate(Assembly assembly)
    {
        RecipeCatalogBindings.Validate(assembly, Members);
        return new GameBindings(assembly).Resolve(Methods);
    }
}

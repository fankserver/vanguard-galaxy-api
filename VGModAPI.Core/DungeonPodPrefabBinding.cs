using System;
using System.Reflection;
namespace VGModAPI.Core;

internal static class DungeonPodPrefabBinding
{
    internal static FieldInfo Resolve(Type manager, Type pod)
    {
        var field = manager.GetField("boardingPodPrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(manager.FullName, "boardingPodPrefab");
        if (field.FieldType != pod) throw new InvalidOperationException("Unexpected boarding pod prefab type.");
        return field;
    }
}

using System;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Runtime;

internal static class TravelArrivalBindings
{
    internal static MethodInfo[] Resolve(Type managerBase)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var root = managerBase.GetMethod("SpaceshipHasArrived", flags, null, Type.EmptyTypes, null);
        if (root == null || !root.IsVirtual || root.IsAbstract || root.ReturnType != typeof(void))
            throw new MissingMethodException(managerBase.FullName, "SpaceshipHasArrived");
        // Patch declarations, including the concrete implementation on the abstract
        // base. Inherited implementations need no additional detour. Base first
        // prevents an override from JIT-inlining an unpatched callee.
        return managerBase.Assembly.GetTypes()
            .Where(managerBase.IsAssignableFrom)
            .Select(type => type.GetMethod("SpaceshipHasArrived", flags, null, Type.EmptyTypes, null))
            .Where(method => method != null && !method.IsAbstract && method.GetBaseDefinition() == root)
            .Select(method => method!)
            .OrderBy(method => Depth(method.DeclaringType!))
            .ThenBy(method => method.DeclaringType!.FullName, StringComparer.Ordinal)
            .ToArray();
    }
    private static int Depth(Type type)
    {
        var depth = 0;
        for (var parent = type.BaseType; parent != null; parent = parent.BaseType) depth++;
        return depth;
    }
}

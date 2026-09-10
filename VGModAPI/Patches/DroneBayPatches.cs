using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

/// <summary>Undeclared bays and every fault run the vanilla launch timing and replacement roll.</summary>
internal static class DroneBayPatches
{
    internal static DroneBayRuntime? Runtime;
    internal static class LaunchDuration
    {
        internal static bool Prefix(object __instance, ref float __result)
        {
            if (Runtime?.LaunchSeconds(__instance) is not { } declared) return true;
            __result = (float)declared;
            return false;
        }
    }
    internal static class Replacement
    {
        internal static bool Prefix(object __instance, int idx, ref object? __result)
        {
            var prefab = Runtime?.ReplacementPrefab(__instance, idx);
            if (prefab == null) return true;
            __result = prefab;
            return false;
        }
    }
}

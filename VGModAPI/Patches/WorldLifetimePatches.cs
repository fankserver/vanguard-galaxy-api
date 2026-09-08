using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLifetimePatches
{
    internal static IWorldLifetimeHookHost? Host = null;
    internal static class Ambient
    {
        internal static bool Prefix(object __instance) => Host?.AllowAmbient(__instance) ?? true;
    }
    internal static class Initialization
    {
        internal static void Postfix(object __instance, ref System.Collections.IEnumerator __result)
        {
            var host = Host;
            if (host != null) __result = new WorldManagerEnumerator(__result, __instance, host);
        }
    }
    internal static class Arrival
    {
        internal static bool Prefix(object __instance) => Host?.AllowManager(__instance) ?? true;
    }
    internal static class Spawn
    {
        internal static void Prefix(object __instance)
        {
            if (!(Host?.AllowManager(__instance) ?? true))
                throw new System.IO.InvalidDataException("Quarantined world content cannot spawn native objects.");
        }
    }
    internal static class Active
    {
        internal static bool Prefix(object __instance) => Host?.AllowUse(__instance) ?? true;
    }
    internal static class CanTravel
    {
        internal static bool Prefix(object __instance, ref bool __result)
        {
            if (Host?.AllowUse(__instance) ?? true) return true;
            __result = false; return false;
        }
    }
    internal static class Route
    {
        internal static bool Prefix(object poi, ref bool __result)
        {
            if (Host?.AllowUse(poi) ?? true) return true;
            __result = false; return false;
        }
    }
    internal static class Remove
    {
        internal static bool Prefix(object poi) => Host?.AllowRemoval(poi) ?? true;
    }
}

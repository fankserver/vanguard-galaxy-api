using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLifetimePatches
{
    internal static IWorldLifetimeHookHost? Host = null;
    internal static class Ambient
    {
        internal static bool Prefix(object __instance) => Host?.AllowAmbient(__instance) ?? true;
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

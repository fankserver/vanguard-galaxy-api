using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLifetimePatches
{
    internal static IWorldLifetimeHookHost? Host = null;
    internal static class Ambient
    {
        internal static bool Prefix(object __instance) => Host?.AllowAmbient(__instance) ?? true;
    }
    internal static class Remove
    {
        internal static bool Prefix(object poi) => Host?.AllowRemoval(poi) ?? true;
    }
}

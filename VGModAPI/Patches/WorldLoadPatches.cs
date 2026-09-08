using System;
using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldLoadPatches
{
    internal static IWorldLoadHookHost? Host = null;

    internal static class Recall
    {
        internal static bool Prefix(object __instance, ref object? __result)
        {
            var host = Host;
            if (host == null || !host.TryRecall(__instance, out var result)) return true;
            __result = result ?? throw new InvalidOperationException("Missing verified world load input.");
            return false;
        }
    }

    internal static class Factory
    {
        internal static void Prefix(object val) => Host?.RequireFactory(val);
    }
}

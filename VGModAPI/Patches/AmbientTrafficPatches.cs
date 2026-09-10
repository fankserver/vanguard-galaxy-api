using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

/// <summary>Suppression skips only the decorative spawn attempt; every failure falls open to vanilla.</summary>
internal static class AmbientTrafficPatches
{
    internal static AmbientTrafficRuntime? Runtime;
    internal static class StationVisitor
    {
        internal static bool Prefix(object __instance, ref bool __result)
        {
            if (Runtime?.SuppressStationVisitor(__instance) != true) return true;
            __result = false; // The spawner loop treats the declined visitor like an unavailable dock.
            return false;
        }
    }
    internal static class GateTraffic
    {
        internal static bool Prefix(object __instance) => Runtime?.SuppressGateTraffic(__instance) != true;
    }
}

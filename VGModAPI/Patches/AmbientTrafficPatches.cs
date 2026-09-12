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
    internal static class WormholeTraffic
    {
        internal static bool Prefix(object __instance) => Runtime?.SuppressWormholeTraffic(__instance) != true;
    }
    /// <summary>Skips the security-patrol creation at an explicitly quieted wormhole, so an owned
    /// wormhole spawns nothing at all; every other POI keeps its vanilla patrol.</summary>
    internal static class SecurityPatrol
    {
        internal static bool Prefix(object __instance) => Runtime?.SuppressQuietWormholePatrol(__instance) != true;
    }
    /// <summary>
    /// Skips the game's first-visit window dressing for owned authored points of interest.
    ///
    /// The game adds a gun platform, an asteroid field, cargo containers and a derelict ship to a wormhole
    /// or jump gate the first time it is visited while it holds no persistables. An authored door is created
    /// deliberately empty, so it always matches that condition and would collect random content it never
    /// declared. Owned content is author-placed only; everything else keeps vanilla dressing.
    /// </summary>
    internal static class WindowDressing
    {
        internal static bool Prefix(object poi) => Runtime?.SuppressWindowDressing(poi) != true;
    }
}

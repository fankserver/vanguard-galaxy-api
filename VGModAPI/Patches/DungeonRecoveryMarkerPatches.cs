using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonRecoveryMarkerPatches
{
    internal static DungeonRecoveryRuntime? Runtime { get; set; }
    internal static class PodSave
    { internal static void Postfix(object __instance, object __0) => Runtime?.SavePod(__instance, __0); }
    internal static class PodLoad
    { internal static void Postfix(object __instance, object __0) => Runtime?.LoadPod(__instance, __0); }
    internal static class LocationSave
    { internal static void Postfix(object __instance, object __0) => Runtime?.SaveLocation(__instance, __0); }
    internal static class LocationLoad
    { internal static void Postfix(object __instance, object __0) => Runtime?.LoadLocation(__instance, __0); }
}

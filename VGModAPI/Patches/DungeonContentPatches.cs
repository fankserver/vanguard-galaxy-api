using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonContentPatches
{
    internal static DungeonContentAdapter? Adapter { get; set; }
    internal static DungeonMarkerJson? Json { get; set; }
    internal static class Serialization
    {
        internal static void Prefix(out DungeonContentAdapter? __state) { __state = Adapter; __state?.BeginSerialization(); }
        internal static System.Exception? Finalizer(DungeonContentAdapter? __state, System.Exception? __exception)
        { __state?.EndSerialization(); return __exception; }
    }
    internal static class SaveLocation
    {
        internal static void Postfix(object __instance, object __0)
        {
            var id = Adapter?.Marker(__instance); if (id.HasValue) Json?.Write(__0, id.Value);
        }
    }
    internal static class LoadLocation
    {
        internal static void Postfix(object __instance, object __0)
        {
            var id = Json?.Read(__0); if (id.HasValue) Adapter?.RestoreMarker(__instance, id.Value);
        }
    }
    internal static class ShipLayout
    {
        internal static void Postfix(object __0, object __1) => Adapter?.ReplaceLayout(__0, __1);
    }
    internal static class WalkCreated
    {
        internal static void Postfix(object __0) => Adapter?.CompleteWalkCreation(__0);
    }
    internal static class WalkLayout
    {
        internal static void Postfix(object __instance, object __0) => Adapter?.ReplaceWalkLayout(__instance, __0);
    }
    internal static class Hazard
    {
        internal static bool Prefix(object __instance) => Adapter?.AllowEffect(__instance, true) != false;
    }
    internal static class Reinforcements
    {
        internal static bool Prefix(object __instance) => Adapter?.AllowEffect(__instance, false) != false;
    }
    internal static class Defenders
    {
        internal static bool Prefix(object __instance) => Adapter?.ReplaceDefenders(__instance) != true;
    }
}

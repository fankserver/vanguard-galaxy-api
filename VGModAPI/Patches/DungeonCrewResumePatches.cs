using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class DungeonCrewResumePatches
{
    internal static DungeonCrewResumeCoordinator? Coordinator;
    internal static class Save
    {
        internal static void Postfix(object __instance, object __result) => Coordinator?.SaveCrew(__instance, __result);
    }
    internal static class Load
    {
        internal static void Postfix(object __0, object __result) => Coordinator?.LoadCrew(__result, __0);
    }
    internal static class SimulationLoad
    {
        internal static void Postfix(object __result, object? __0 = null) => Coordinator?.LoadSimulation(__result, __0);
    }
    internal static class SimulationSave
    {
        internal static void Postfix(object __instance, object __result) => Coordinator?.SaveSimulation(__instance, __result);
        internal static void Prefix(object __instance)
        {
            if (Coordinator?.CanTick(__instance) == false) throw new System.InvalidOperationException("Cannot serialize an invalid dungeon restore.");
        }
    }
    internal static class Tick
    {
        internal static bool Prefix(object __instance) => Coordinator?.CanTick(__instance) ?? true;
    }
}

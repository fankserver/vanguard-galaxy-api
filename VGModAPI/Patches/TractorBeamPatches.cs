using System.Collections;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class TractorBeamPatches
{
    internal static TractorBeamRuntime? Runtime;
    internal static class Available
    {
        internal static void Postfix(object __instance, bool bonus, ref object? __result)
            => __result = Runtime?.Borrow(__instance, bonus, __result) ?? __result;
    }
    internal static class Targets
    {
        internal static void Postfix(object __instance, IEnumerable targets) => Runtime?.TopUp(__instance, targets);
    }
    internal static class ModuleStats
    {
        internal static void Postfix(object __instance) => Runtime?.AddModuleDescription(__instance);
    }
    internal static class MasteryTooltip
    {
        internal static void Postfix(object __instance, object tooltip) => Runtime?.AddMasteryDescription(__instance, tooltip);
    }
}

using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class TooltipPatches
{
    internal static TooltipRuntime? Runtime;
    internal static class ModuleStats
    {
        internal static void Postfix(object __instance) => Runtime?.AddModuleDescription(__instance);
    }
    internal static class MasteryTooltip
    {
        internal static void Postfix(object __instance, object tooltip) => Runtime?.AddMasteryDescription(__instance, tooltip);
    }
    internal static class ItemContent
    {
        internal static void Postfix(object __instance) => Runtime?.AddItemDescription(__instance);
    }
}

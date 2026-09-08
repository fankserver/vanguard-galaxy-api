using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class BoardingTacticalPatches
{
    internal static BoardingTacticalAdapter? Adapter = null;
    internal static class VoidAction
    {
        internal static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args)
            => Adapter?.ValidateNative(__instance, __originalMethod.Name, __args) ?? true;
    }
    internal static class BoolAction
    {
        internal static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args, ref bool __result)
        {
            if (Adapter?.ValidateNative(__instance, __originalMethod.Name, __args) ?? true) return true;
            __result = false; return false;
        }
    }
}

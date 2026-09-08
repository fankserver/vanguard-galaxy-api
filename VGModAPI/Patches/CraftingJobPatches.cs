using System;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.Runtime;

internal static class CraftingJobPatches
{
    internal static CraftingJobObserver? Observer;
    internal static IReadOnlyDictionary<MethodBase, string> Keys = new Dictionary<MethodBase, string>();
    internal static void Prefix(MethodBase __originalMethod, object __instance, object[] __args, out CraftingJobObserver.Scope? __state)
    {
        __state = Keys.TryGetValue(__originalMethod, out var key) ? Observer?.Begin(key, __instance, __args) : null;
    }
    internal static Exception? VoidFinalizer(CraftingJobObserver.Scope? __state, Exception? __exception)
    { Observer?.End(__state, null, __exception); return __exception; }
    internal static Exception? BoolFinalizer(CraftingJobObserver.Scope? __state, bool __result, Exception? __exception)
    { Observer?.End(__state, __result, __exception); return __exception; }
    internal static Exception? ObjectFinalizer(CraftingJobObserver.Scope? __state, object? __result, Exception? __exception)
    { Observer?.End(__state, __result, __exception); return __exception; }
}

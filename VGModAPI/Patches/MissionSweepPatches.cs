using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class MissionSweepPatches
{
    internal static void Prefix(object __instance, MethodBase __originalMethod, out MissionAdapter.Sweep? __state)
    {
        MissionAdapter.Sweep? state = null;
        var adapter = MissionPatches.Adapter;
        adapter?.Guard(() => state = adapter.BeginSweep(__originalMethod.DeclaringType?.FullName == "Source.Player.GamePlayer" ? __instance : null));
        __state = state;
    }
    internal static Exception? Finalizer(MissionAdapter.Sweep? __state, Exception? __exception)
    {
        var adapter = MissionPatches.Adapter;
        if (__state != null) adapter?.Guard(() => adapter.EndSweep(__state));
        return __exception;
    }
}

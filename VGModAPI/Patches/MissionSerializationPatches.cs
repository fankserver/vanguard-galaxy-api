using System;
using VGModAPI.Core;

namespace VGModAPI.Patches;

internal static class MissionSerializationPatches
{
    internal static void Prefix(out MissionSerializationTracker.Capture? __state)
    {
        MissionSerializationTracker.Capture? state = null;
        MissionPatches.Adapter?.Guard(() => state = MissionPatches.Adapter.BeginSerialization());
        __state = state;
    }
    internal static Exception? Finalizer(MissionSerializationTracker.Capture? __state, object __result, Exception? __exception)
    {
        if (__state != null && __exception == null) MissionPatches.Adapter?.Guard(() => MissionPatches.Adapter.EndSerialization(__state, __result));
        return __exception;
    }
}

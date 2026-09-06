using System;
using System.Reflection;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class MissionPatches
{
    internal static MissionAdapter? Adapter;
    internal static void Prefix(object __instance, MethodBase __originalMethod, object[] __args, out MissionAdapter.Call? __state)
    {
        MissionAdapter.Call? state = null;
        Adapter?.Guard(() =>
        {
            state = __originalMethod.Name switch
            {
                "AddMissionWithLog" => Adapter.Begin("accept", __instance, __args[0]),
                "RemoveMission" => Adapter.Begin("remove", __instance, __args[0], (bool)__args[1]),
                "ArchiveMission" => Adapter.Begin("archive", __instance, null, definition: (string?)__args[0]),
                "ClaimRewards" => Adapter.Begin("claim", null, __instance, (bool)__args[0]),
                "MissionFailed" => Adapter.Begin("fail", null, __instance),
                "OnMissionStart" => Adapter.Begin("start", null, __instance),
                _ => throw new InvalidOperationException("Unknown mission hook.")
            };
        });
        __state = state;
    }
    internal static Exception? Finalizer(MissionAdapter.Call? __state, Exception? __exception)
    {
        if (__state != null) Adapter?.Guard(() => Adapter.End(__state));
        return __exception;
    }
}

using System;
using HarmonyLib;

namespace VGModAPI.QualificationGuard;

public sealed partial class Plugin
{
    private static bool _missionProbesInstalled;
    private static object? _missionClearTarget, _missionFocusTarget;
    private static Exception? _missionClearError, _missionFocusError;
    private static void InstallMissionProbeGuards(Harmony harmony)
    {
        // Install tiny callees before the API patches/JITs their callers.
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Source.Galaxy.GalaxyMapData"), "ClearSectors"),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(StopMissionMapClear)));
        harmony.Patch(AccessTools.Method(AccessTools.TypeByName("FocusedMissionHandler"), "SetMission", new[] { AccessTools.TypeByName("Source.MissionSystem.Mission") }),
            prefix: new HarmonyMethod(typeof(Plugin), nameof(StopMissionFocus)));
        _missionProbesInstalled = true;
    }
    public static void ArmMissionProbe(string boundary, object? target, Exception? error)
    {
        if (!_missionProbesInstalled) throw new InvalidOperationException("Early mission probe guards unavailable.");
        if ((target == null) != (error == null)) throw new ArgumentException("Probe target and error must be paired.");
        switch (boundary)
        {
            case "clear": _missionClearTarget = target; _missionClearError = error; break;
            case "focus": _missionFocusTarget = target; _missionFocusError = error; break;
            default: throw new ArgumentException("Unknown mission probe boundary.");
        }
    }
    private static void StopMissionMapClear(object __instance)
    { if (_missionClearError != null && ReferenceEquals(__instance, _missionClearTarget)) throw _missionClearError; }
    private static void StopMissionFocus(object __0)
    { if (_missionFocusError != null && ReferenceEquals(__0, _missionFocusTarget)) throw _missionFocusError; }
}

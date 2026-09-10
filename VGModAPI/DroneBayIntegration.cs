using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private DroneBayService? _droneBays;
    private DroneBayRuntime? _droneBayRuntime;
    private Harmony? _droneBayHarmony;

    private void InstallDroneBays(Assembly assembly)
    {
        _droneBays ??= new DroneBayService(_hub!);
        try
        {
            var methods = DroneBayBindings.Validate(assembly);
            var shipType = assembly.GetType("Behaviour.Unit.SpaceShip", true)!;
            var bayType = assembly.GetType(DroneBayBindings.Bay, true)!;
            _droneBayRuntime = new DroneBayRuntime(assembly, _hub!, _droneBays,
                () => UnityEngine.Object.FindObjectsByType(shipType),
                unit => unit is UnityEngine.Component component ? component.GetComponentInChildren(bayType) : null,
                drone =>
                {
                    if (drone is not UnityEngine.Component component || component == null) return;
                    component.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(component.gameObject);
                },
                notice => Logger.LogInfo(notice),
                error => Logger.LogError(error));
            DroneBayPatches.Runtime = _droneBayRuntime;
            _droneBayHarmony = new Harmony(ModApi.PluginId + ".drone-bays");
            _droneBayHarmony.Patch(methods["droneLaunchDuration"],
                prefix: new HarmonyMethod(typeof(DroneBayPatches.LaunchDuration), "Prefix"));
            _droneBayHarmony.Patch(methods["droneReplacementRoll"],
                prefix: new HarmonyMethod(typeof(DroneBayPatches.Replacement), "Prefix"));
            _droneBays.SetAvailable(true);
        }
        catch (Exception error) { TeardownDroneBays(); Logger.LogError(error); }
    }

    private void TeardownDroneBays()
    {
        DroneBayPatches.Runtime = null;
        _droneBayRuntime = null;
        try { _droneBayHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _droneBayHarmony = null;
        _droneBays?.SetAvailable(false);
    }
}

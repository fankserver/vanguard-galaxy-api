using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

/// <summary>Equipment targeting policy: the single native hook pair behind IEquipmentService.
/// TractorBeamRuntime keeps its name because it binds the game's own TractorBeam types.</summary>
public sealed partial class Plugin
{
    private EquipmentService? _equipment;
    private Harmony? _equipmentHarmony;
    private void InstallEquipmentTargeting(Assembly assembly)
    {
        _equipment ??= new EquipmentService(_hub!);
        try
        {
            var runtime = new TractorBeamRuntime(assembly, _equipment, error => Logger.LogError(error));
            _equipmentHarmony = new Harmony(ModApi.PluginId + ".equipment-targeting");
            TractorBeamPatches.Runtime = runtime;
            _equipmentHarmony.Patch(runtime.AvailableBeam, postfix: new HarmonyMethod(typeof(TractorBeamPatches.Available), "Postfix"));
            _equipmentHarmony.Patch(runtime.UpdateTargets, postfix: new HarmonyMethod(typeof(TractorBeamPatches.Targets), "Postfix"));
            _equipment.SetAvailable(true);
        }
        catch (Exception error) { TeardownEquipmentTargeting(); Logger.LogError(error); }
    }
    private void TeardownEquipmentTargeting()
    {
        TractorBeamPatches.Runtime = null;
        try { _equipmentHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _equipmentHarmony = null;
        _equipment?.SetAvailable(false);
    }
}

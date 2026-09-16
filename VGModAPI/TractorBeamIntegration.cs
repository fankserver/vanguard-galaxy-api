using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private EquipmentService? _equipment;
    private SkillTreeService? _skillTrees;
    private TooltipService? _tooltips;
    private Harmony? _tractorHarmony;
    private void InstallTractorBeams(Assembly assembly)
    {
        _equipment ??= new EquipmentService(_hub!);
        _skillTrees ??= new SkillTreeService(_hub!);
        _tooltips ??= new TooltipService(_hub!);
        try
        {
            var skills = new SkillTreeRuntime(assembly);
            var runtime = new TractorBeamRuntime(assembly, _equipment, _tooltips, skills, error => Logger.LogError(error));
            _tractorHarmony = new Harmony(ModApi.PluginId + ".tractor-beams");
            TractorBeamPatches.Runtime = runtime;
            _tractorHarmony.Patch(runtime.AvailableBeam, postfix: new HarmonyMethod(typeof(TractorBeamPatches.Available), "Postfix"));
            _tractorHarmony.Patch(runtime.UpdateTargets, postfix: new HarmonyMethod(typeof(TractorBeamPatches.Targets), "Postfix"));
            _tractorHarmony.Patch(runtime.ModuleStats, postfix: new HarmonyMethod(typeof(TractorBeamPatches.ModuleStats), "Postfix"));
            _tractorHarmony.Patch(runtime.MasteryTooltip, postfix: new HarmonyMethod(typeof(TractorBeamPatches.MasteryTooltip), "Postfix"));
            _skillTrees.Bind(skills.Get);
            _equipment.SetAvailable(true); _tooltips.SetAvailable(true);
        }
        catch (Exception error) { TeardownTractorBeams(); Logger.LogError(error); }
    }
    private void TeardownTractorBeams()
    {
        TractorBeamPatches.Runtime = null;
        try { _tractorHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _tractorHarmony = null;
        _equipment?.SetAvailable(false); _skillTrees?.Bind(null); _tooltips?.SetAvailable(false);
    }
}

using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

/// <summary>Tooltip extensions and the skill-tree reader behind ISkillTreeService: one Harmony
/// instance per seam, bound fail-closed.</summary>
public sealed partial class Plugin
{
    private SkillTreeService? _skillTrees;
    private TooltipService? _tooltips;
    private Harmony? _tooltipsHarmony;
    private void InstallTooltips(Assembly assembly)
    {
        _tooltips ??= new TooltipService(_hub!);
        _skillTrees ??= new SkillTreeService(_hub!);
        try
        {
            var skills = new SkillTreeRuntime(assembly);
            var runtime = new TooltipRuntime(assembly, _tooltips, skills, error => Logger.LogError(error));
            _tooltipsHarmony = new Harmony(ModApi.PluginId + ".tooltips");
            TooltipPatches.Runtime = runtime;
            foreach (var builder in runtime.ModuleStatBuilders)
                _tooltipsHarmony.Patch(builder, postfix: new HarmonyMethod(typeof(TooltipPatches.ModuleStats), "Postfix"));
            _tooltipsHarmony.Patch(runtime.MasteryTooltip, postfix: new HarmonyMethod(typeof(TooltipPatches.MasteryTooltip), "Postfix"));
            foreach (var fill in runtime.ContentFills)
                _tooltipsHarmony.Patch(fill, postfix: new HarmonyMethod(typeof(TooltipPatches.ItemContent), "Postfix"));
            _skillTrees.Bind(skills.Get);
            _tooltips.SetAvailable(true);
        }
        catch (Exception error) { TeardownTooltips(); Logger.LogError(error); }
    }
    private void TeardownTooltips()
    {
        TooltipPatches.Runtime = null;
        try { _tooltipsHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _tooltipsHarmony = null;
        _tooltips?.SetAvailable(false); _skillTrees?.Bind(null);
    }
}

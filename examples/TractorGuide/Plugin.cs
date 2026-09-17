using System;
using BepInEx;
using BepInEx.Configuration;
using VGModAPI;

// Example author package: the three #313 surfaces in one small, honest mod.
//  - Equipment: borrow one manual beam for the autopilot when ExtraAutoBeam is on.
//  - Tooltips.RegisterShipModule: a mastery hint on tractor module stat lists.
//  - Tooltips.RegisterSkillTree: what Engineering actually does for the tractor.
//  - Tooltips.RegisterItem: opt-in highlight for one item by identifier.
// Policy and text stay mod-owned; all native beam, crew and cargo guards stay native.
namespace TractorGuide
{
    [BepInPlugin(Id, Name, Version)]
    [BepInDependency(ModApi.PluginId)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "vgmodapi.example.tractor-guide";
        public const string Name = "Tractor Guide";
        public const string Version = "1.0.0";

        private readonly ConfigEntry<bool> _extraAutoBeam;
        private readonly ConfigEntry<string> _highlightItem;
        private IDisposable? _equipment;
        private IDisposable? _moduleTooltip;
        private IDisposable? _itemTooltip;
        private IDisposable? _treeTooltip;

        public Plugin()
        {
            _extraAutoBeam = Config.Bind("Tractor", "ExtraAutoBeam", false,
                "Let the tractor autopilot borrow one manual beam slot when all automatic beams are busy.");
            _highlightItem = Config.Bind("Tractor", "HighlightItem", "",
                "Item identifier (leave empty to disable) whose tooltip gets a tractor tip.");
        }

        private void Start()
        {
            ModServices api;
            try { api = ModApi.Services; }
            catch (InvalidOperationException) { Logger.LogWarning("VGModAPI services are not available; TractorGuide does nothing."); return; }

            _equipment = api.Equipment.ConfigurePlayerTractorModules(Id, module =>
            {
                // Abstain (null) to keep vanilla behavior; only tractors with a spare manual beam qualify.
                if (!_extraAutoBeam.Value || module.BeamCount <= 0 || module.ManualBeamCount <= 0) return null;
                return new TractorTargeting(module.BeamCount + 1);
            });

            _moduleTooltip = api.Tooltips.RegisterShipModule(Id, (module, tip) =>
            {
                if (module.Kind != ShipModuleKind.Tractor) return;
                var tree = api.SkillTrees.Get(CommanderSpecialization.Engineering);
                tip.AddLine(tree == null
                    ? "Tractor autopilot (TractorGuide)"
                    : $"Autopilot mastery {tree.MasteryLevel}/{tree.MaximumLevel} (TractorGuide)", TooltipTextStyle.Bonus);
            });

            _treeTooltip = api.Tooltips.RegisterSkillTree(Id, (tree, tip) =>
            {
                if (tree.Specialization == CommanderSpecialization.Engineering)
                    tip.AddLine("Engineering also powers the tractor autopilot (TractorGuide)", TooltipTextStyle.Details);
            });

            _itemTooltip = api.Tooltips.RegisterItem(Id, (item, tip) =>
            {
                var wanted = _highlightItem.Value;
                if (wanted.Length > 0 && item.Identifier == wanted)
                    tip.AddLine("Easy to ferry with a tractor beam (TractorGuide)", TooltipTextStyle.Details);
            });

            Logger.LogInfo($"TractorGuide registered (availability: equipment={api.Equipment.Availability.IsAvailable}, " +
                $"skillTrees={api.SkillTrees.Availability.IsAvailable}, tooltips={api.Tooltips.Availability.IsAvailable}).");
        }

        private void OnDestroy()
        {
            _equipment?.Dispose(); _moduleTooltip?.Dispose(); _itemTooltip?.Dispose(); _treeTooltip?.Dispose();
        }
    }
}

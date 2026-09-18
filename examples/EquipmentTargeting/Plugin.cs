using System;
using BepInEx;
using BepInEx.Configuration;
using VGModAPI;

// Example author package for "equipment targeting" (see PR #313): your mod answers one
// targeting question for the game's tractor autopilot (always on, so it is observable in play). The API owns the only hook into the game;
// target eligibility, crew, cargo and occupied-beam protections stay native. Tooltip and settings
// showcases live in the UiSurfaces and StationCommerce examples.
namespace EquipmentTargeting
{
    [BepInPlugin(Id, Name, Version)]
    [BepInDependency(ModApi.PluginId)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "vgmodapi.example.equipment-targeting";
        public const string Name = "Equipment Targeting example";
        public const string Version = "1.0.0";

        private readonly ConfigEntry<bool> _extraAutoBeam;
        private IDisposable? _equipment;

        public Plugin()
        {
            // Presentation-only row: it demonstrates typed settings plumbing without gating anything.
            // The example's contribution stays visible in the Mods menu without changing gameplay.
            _extraAutoBeam = Config.Bind("Tractor", "ExtraAutoBeam", true,
                "Demo setting (presentation only): autopilot borrowing is always active in this example.");
        }

        private void Start()
        {
            ModServices api;
            try { api = ModApi.Services; }
            catch (InvalidOperationException) { Logger.LogWarning("VGModAPI services are not available; the example does nothing."); return; }
            _equipment = api.Equipment.ConfigurePlayerTractorModules(Id, module =>
            {
                // Only tractors with a spare manual beam qualify; otherwise abstain (null) keeps vanilla.
                if (module.BeamCount <= 0 || module.ManualBeamCount <= 0) return null;
                return new TractorTargeting(module.BeamCount + 1);
            });
            Logger.LogInfo($"Equipment Targeting registered (equipment available: {api.Equipment.Availability.IsAvailable}).");
        }

        private void OnDestroy() => _equipment?.Dispose();
    }
}

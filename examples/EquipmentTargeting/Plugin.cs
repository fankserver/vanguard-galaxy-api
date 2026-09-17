using System;
using BepInEx;
using BepInEx.Configuration;
using VGModAPI;

// Example author package for "equipment targeting" (see PR #313): your mod answers one
// targeting question for the game's tractor autopilot. The API owns the only hook into the game;
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
            _extraAutoBeam = Config.Bind("Tractor", "ExtraAutoBeam", false,
                "Let the tractor autopilot borrow one manual beam slot when all automatic beams are busy.");
        }

        private void Start()
        {
            ModServices api;
            try { api = ModApi.Services; }
            catch (InvalidOperationException) { Logger.LogWarning("VGModAPI services are not available; the example does nothing."); return; }
            _equipment = api.Equipment.ConfigurePlayerTractorModules(Id, module =>
            {
                // Abstain (null) to keep vanilla behavior; only tractors with a spare manual beam qualify.
                if (!_extraAutoBeam.Value || module.BeamCount <= 0 || module.ManualBeamCount <= 0) return null;
                return new TractorTargeting(module.BeamCount + 1);
            });
            Logger.LogInfo($"Equipment Targeting registered (equipment available: {api.Equipment.Availability.IsAvailable}).");
        }

        private void OnDestroy() => _equipment?.Dispose();
    }
}

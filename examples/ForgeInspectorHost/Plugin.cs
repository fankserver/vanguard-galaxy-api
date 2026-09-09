using System;
using BepInEx;
using UnityEngine;
using VGModAPI;

namespace ForgeInspectorHost;

/// <summary>Thin Unity host; recipe and presentation logic stays in the public-contract-only example.</summary>
[BepInPlugin("vgmodapi.example.forge-inspector", "Forge Inspector Example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.2.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private ForgeInspector.Inspector? _inspector;
    private float _nextAttempt;
    private bool _warned;
    private void Update()
    {
        if (_inspector != null || Time.unscaledTime < _nextAttempt) return;
        _nextAttempt = Time.unscaledTime + .5f;
        ModServices services;
        try { services = ModApi.Services; }
        catch (InvalidOperationException) { WarnUnavailable(); return; }
        if (!services.Lifecycle.SessionTracking.Availability.IsAvailable || !services.ForgeUi.Availability.IsAvailable
            || !services.Recipes.Availability.IsAvailable || !services.RecipeQuotes.Availability.IsAvailable || !services.Hud.Availability.IsAvailable)
        { WarnUnavailable(); return; }
        try
        {
            _inspector = new("vgmodapi.example.forge-inspector", services.Lifecycle, services.ForgeUi,
                services.Recipes, services.RecipeQuotes, services.Hud);
        }
        catch (Exception error) { Logger.LogError(error); }
    }
    private void WarnUnavailable()
    {
        if (!_warned) Logger.LogWarning("Forge Inspector requires available session, recipe and HUD services.");
        _warned = true;
    }
    private void OnDestroy() { _inspector?.Dispose(); _inspector = null; }
}

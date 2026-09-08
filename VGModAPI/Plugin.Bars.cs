using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private BarContentService? _bars;
    private BarRuntimeHost? _barHost;
    private Harmony? _barHarmony;
    private ConfigEntry<string>? _barPermissionConfig;
    private sealed class BarPermissions
    {
        internal readonly HashSet<string> Allowed;
        internal BarPermissions(string text) => Allowed = new HashSet<string>(text.Split(',').Select(value => value.Trim())
            .Where(value => value.Length > 0), StringComparer.Ordinal);
    }
    private volatile BarPermissions _barPermissions = new("");

    private void UpdateBarPermissions(object? sender, EventArgs args)
    {
        // Replacing the entire immutable snapshot also replaces the permission epoch. Readers
        // never reuse an old token when grants change, including changes inside provider callbacks.
        _barPermissions = new BarPermissions(_barPermissionConfig?.Value ?? "");
    }

    private void InitializeBars()
    {
        ModApi.Bars = null;
        if (!Config.Bind("Bars", "Enabled", false, "Experimental API-owned bar rosters. Use disposable saves until qualified.").Value)
        { _hub!.SetCapability("owned-bars", false, "Disabled by configuration."); return; }
        if (_persistence == null || !_hub!.Capabilities.Any(capability => capability.Name == "session-lifecycle" && capability.Available))
        { _hub!.SetCapability("owned-bars", false, "Inspected lifecycle and API-managed saves are required."); return; }
        if (BarPatches.Host != null)
        { _hub.SetCapability("owned-bars", false, "Process-lived contact guards are already installed; restart the game."); return; }
        try
        {
            _barPermissionConfig = Config.Bind("Bars", "ExclusiveProviders", "", "Comma-separated exact plugin IDs permitted to claim exclusive station rosters. Conflicting claims are denied.");
            UpdateBarPermissions(null, EventArgs.Empty);
            _barPermissionConfig.SettingChanged += UpdateBarPermissions;
            var assembly = Assembly.Load("Assembly-CSharp");
            var native = new BarNativeBindings(assembly);
            var bars = new BarContentService(_persistence, _hub, StoryHostAuthentication.Resolve,
                plugin => _barPermissions.Allowed.Contains(plugin), _hub.CheckThread, () => _barPermissions,
                (owner, error) => Logger.LogError("Bar observer '" + owner + "' failed: " + error));
            _bars = bars;
            var story = _story;
            var noStory = new object();
            _barHost = new BarRuntimeHost(bars, native.World, native.Contacts, native.Serialization,
                station => _hub.CurrentSession is { } session ? bars.Plan(session.Id, station,
                    (id, occurrence) => story?.IsBarMissionReady(session.Id, id, occurrence) == true,
                    () => story?.BarDependencyStamp() ?? noStory) : null,
                bars.CanSerializeCurrent, bars.CanMutateCurrent, _hub.CheckThread,
                error => { ModApi.Bars = null; _hub.SetCapability("owned-bars", false, "Bar adapter fault; content guards remain active."); Logger.LogError(error); });
            var targets = new GameBindings(assembly).Resolve(BindingCatalog.Bars);
            var patches = new Dictionary<string, Type>
            {
                ["barRefresh"] = typeof(BarPatches.Refresh), ["barSerialize"] = typeof(BarPatches.Serialize),
                ["barPatronSerialize"] = typeof(BarPatches.PatronSerialize), ["barInteract"] = typeof(BarPatches.Interact)
            };
            // A distinct owner prevents general plugin teardown from removing protection for
            // contact objects still held by stale UI buttons or uncertain retained rosters.
            _barHarmony = new Harmony(ModApi.PluginId + ".bars");
            var installs = new List<Action>();
            foreach (var binding in BindingCatalog.Bars)
            {
                var patch = patches[binding.Key];
                HarmonyMethod? Hook(string name)
                {
                    var method = patch.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
                    return method == null ? null : new HarmonyMethod(method);
                }
                installs.Add(() => _barHarmony.Patch(targets[binding.Key], prefix: Hook("Prefix"), finalizer: Hook("Finalizer")));
            }
            BarHookInstallation.Install(installs, _barHarmony.UnpatchSelf);
            BarPatches.Host = _barHost;
            ModApi.Bars = bars;
            _hub.SetCapability("owned-bars", true, "Experimental owned bar rosters; native qualification pending. Mission-linked contributions require additional readiness integration.");
        }
        catch (Exception error)
        {
            StopBars();
            if (BarPatches.Host == null) _barHarmony?.UnpatchSelf();
            _hub!.SetCapability("owned-bars", false, "Bar initialization failed: " + error.GetType().Name);
            Logger.LogError(error);
        }
    }

    private void StopBars()
    {
        ModApi.Bars = null;
        if (_barPermissionConfig != null) _barPermissionConfig.SettingChanged -= UpdateBarPermissions;
        try
        {
            if (_barHost != null && !_barHost.Stop()) Logger.LogWarning("Bar restoration is uncertain; content guards remain installed.");
        }
        catch (Exception error) { Logger.LogError("Bar restoration failed; content guards remain installed: " + error); }
        finally { _bars?.Dispose(); _bars = null; }
        // Do not clear BarPatches.Host or unpatch the bars owner: stale contacts must never
        // fall through to Salesman behavior, even after successful roster restoration.
    }
}

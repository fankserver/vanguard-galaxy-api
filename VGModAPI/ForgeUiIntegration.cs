using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private ForgeUiService? _forgeUi;
    private ForgeUiRuntime? _forgeUiRuntime;
    private RecipeCatalogNativeSource? _forgeUiSource;
    private Harmony? _forgeUiHarmony;
    private void InstallForgeUi(Assembly assembly, RecipeCatalogNativeSource source)
    {
        try
        {
            var methods = ForgeUiBindings.Validate(assembly);
            _forgeUiSource = source;
            source.UiSession = () => _hub?.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _adapter?.IsBoundPlayer(source.NativePlayer) == true ? _hub.CurrentSession.Id : null;
            source.UiActive = value => value is UnityEngine.Behaviour component && component != null && component.isActiveAndEnabled;
            source.UiAssetAlive = value => value is UnityEngine.Object asset && asset != null;
            source.UiBelongsTo = (value, parent) => value is UnityEngine.Component child && child != null && parent is UnityEngine.Component root && root != null && child.transform.IsChildOf(root.transform);
            _forgeUi = new(_hub!, source, (owner, error) => Logger.LogError(owner + ": " + error));
            source.UiDispatching = () => _forgeUi?.IsDispatchingCallbacks == true;
            _forgeUiRuntime = new(_forgeUi, source, error => Logger.LogError(error));
            ForgeUiPatches.Runtime = _forgeUiRuntime;
            _forgeUiHarmony = new Harmony(ModApi.PluginId + ".forge-ui");
            var finalizer = new HarmonyMethod(typeof(ForgeUiPatches).GetMethod("Finalizer", BindingFlags.Static | BindingFlags.NonPublic));
            foreach (var spec in ForgeUiBindings.Methods) _forgeUiHarmony.Patch(methods[spec.Key], finalizer: finalizer);
            _forgeUi.SetAvailable(true);
        }
        catch (Exception error) { TeardownForgeUi(); Logger.LogError(error); }
    }
    private void TeardownForgeUi()
    {
        ForgeUiPatches.Runtime = null;
        _forgeUiRuntime?.Dispose(); _forgeUiRuntime = null;
        _forgeUi?.Dispose(); _forgeUi = null;
        if (_forgeUiSource != null)
        {
            _forgeUiSource.ClearUi(); _forgeUiSource.UiSession = null; _forgeUiSource.UiDispatching = null; _forgeUiSource.UiActive = null;
            _forgeUiSource.UiBelongsTo = null; _forgeUiSource.UiAssetAlive = null; _forgeUiSource = null;
        }
        try { _forgeUiHarmony?.UnpatchSelf(); } catch (Exception error) { Logger.LogError(error); }
        _forgeUiHarmony = null;
    }
}

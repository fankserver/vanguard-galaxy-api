using System;
using System.Reflection;
using HarmonyLib;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private GameplayUiService? _gameplayUi;
    private GameplayUiRuntime? _gameplayUiRuntime;
    private Harmony? _gameplayUiHarmony;

    private void InstallGameplayUi(Assembly assembly)
    {
        _gameplayUi ??= new GameplayUiService(_hub!);
        try
        {
            var methods = GameplayUiBindings.Validate(assembly);
            _gameplayUiRuntime = new GameplayUiRuntime(_gameplayUi, assembly, () =>
            {
                var current = _hub!.CurrentSession;
                return current != null && current.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized &&
                    _adapter?.IsBoundPlayer(_adapter.Bindings.CurrentPlayer) == true ? current.Id : (Guid?)null;
            }, _hub!.CheckThread);
            _gameplayUiHarmony = new Harmony(ModApi.PluginId + ".gameplay-ui");
            GameplayUiPatches.Runtime = _gameplayUiRuntime;
            _gameplayUiHarmony.Patch(methods["uiAwake"], finalizer: new HarmonyMethod(typeof(GameplayUiPatches.Awake), "Finalizer"));
            _gameplayUiHarmony.Patch(methods["uiStart"],
                prefix: new HarmonyMethod(typeof(GameplayUiPatches.Start), "Prefix"),
                postfix: new HarmonyMethod(typeof(GameplayUiPatches.Start), "Postfix"),
                finalizer: new HarmonyMethod(typeof(GameplayUiPatches.Start), "Finalizer"));
            _gameplayUi.SetAvailable(true);
        }
        catch (Exception error) { TeardownGameplayUi(); Logger.LogError(error); }
    }

    private void TeardownGameplayUi()
    {
        GameplayUiPatches.Runtime = null;
        _gameplayUiRuntime?.Dispose(); _gameplayUiRuntime = null;
        try { _gameplayUiHarmony?.UnpatchSelf(); }
        catch (Exception error) { Logger.LogError(error); }
        _gameplayUiHarmony = null;
        _gameplayUi?.SetAvailable(false);
    }
}

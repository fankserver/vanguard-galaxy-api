using System;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private HudService? _hudService;
    private HudRuntime? _hudRuntime;
    private void InstallHud(Assembly assembly)
    {
        try
        {
            var methods = HudBindings.Validate(assembly);
            _hudService = new(_hub!, (owner, error) => Logger.LogError(owner + ": " + error));
            _hudRuntime = new(_hudService, assembly, methods,
                () => _hub?.CurrentSession?.Phase == SessionPhase.GameplayInitialized && _adapter?.IsBoundPlayer(_adapter.Bindings.CurrentPlayer) == true ? _hub.CurrentSession.Id : null,
                error => Logger.LogError(error));
            _hudService.SetAvailable(true); ModApi.Hud = _hudService;
        }
        catch (Exception error) { TeardownHud(); Logger.LogError(error); }
    }
    private void TeardownHud()
    {
        _hudRuntime?.Dispose(); _hudRuntime = null;
        _hudService?.Dispose(); _hudService = null; ModApi.Hud = null;
    }
}

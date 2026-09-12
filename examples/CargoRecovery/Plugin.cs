using System;
using BepInEx;
using VGModAPI;
namespace CargoRecovery;

[BepInPlugin("vgmodapi.example.cargo", "Cargo recovery example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
    private CargoAuthorSession? _session;

    // Wiring is deferred to Start() for the same reason across every example in this repository:
    // BepInEx only populates Chainloader.PluginInfos[].Instance after Awake returns. Dungeon content
    // is keyed by a plain plugin-id string rather than an authenticated instance, so Awake would work
    // here — the examples still use one consistent rule so nothing depends on which identity a
    // particular service happens to use.
    private void Start()
    {
        var reward = Config.Bind("Content", "RewardItemId", "", "Existing game item identifier for shipment rewards. Required; no content is registered while blank.").Value;
        try
        {
            _session = new CargoAuthorSession(reward, ModApi.Services.Lifecycle, ModApi.Services.DungeonOperations, ModApi.Services.Dungeons,
                ModApi.Services.DungeonPanel, ModApi.Services.DungeonCommands, ModApi.Services.DungeonTactics, ModApi.Services.DungeonSettlement,
                message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        }
        catch (Exception error) { Logger.LogError(error); }
    }
    private void OnDestroy() { _session?.Dispose(); _session = null; }
}

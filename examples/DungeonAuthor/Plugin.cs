using System;
using BepInEx;
using VGModAPI;
namespace DungeonAuthor;

[BepInPlugin("vgmodapi.example.cargo", "Cargo recovery example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.42")]
public sealed class Plugin : BaseUnityPlugin
{
    private CargoAuthorSession? _session;
    private void Awake()
    {
        var reward = Config.Bind("Content", "RewardItemId", "", "Existing game item identifier for shipment rewards. Required; no content is registered while blank.").Value;
        try
        {
            _session = new CargoAuthorSession(reward, ModApi.Services.Lifecycle, ModApi.Services.Boarding, ModApi.Dungeons,
                ModApi.Services.DungeonPanel, ModApi.Services.BoardingCommands, ModApi.Services.BoardingTactics, ModApi.Services.DungeonSettlement,
                message => Logger.LogInfo(message), message => Logger.LogWarning(message));
        }
        catch (Exception error) { Logger.LogError(error); }
    }
    private void OnDestroy() { _session?.Dispose(); _session = null; }
}

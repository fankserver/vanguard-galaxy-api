using System;
using System.Collections.Generic;
using System.Linq;
using AuthoredDungeon;
using BepInEx;
using VGModAPI;
namespace DungeonAuthor;

[BepInPlugin(Id, "Cargo recovery example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.42")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "vgmodapi.example.cargo";
    private readonly List<IDisposable> _leases = new();
    private readonly Dictionary<BoardingHandle, CargoRecoveryPanel> _panels = new();
    private CargoRecovery? _author;
    private void Awake()
    {
        var reward = Config.Bind("Content", "RewardItemId", "", "Existing game item identifier for shipment rewards. Required; no content is registered while blank.").Value;
        var lifecycle = ModApi.Current; var boarding = ModApi.Boarding; var content = ModApi.Dungeons;
        var panel = ModApi.DungeonPanel; var commands = ModApi.BoardingCommands;
        var tactics = ModApi.BoardingTactics; var settlement = ModApi.DungeonSettlement;
        if (lifecycle == null || boarding == null || content == null || string.IsNullOrWhiteSpace(reward))
        { Logger.LogWarning("Cargo example requires configured RewardItemId and available boarding/dungeon content."); return; }
        try
        {
            _author = new CargoRecovery(content, Id, reward);
            _leases.Add(lifecycle.Subscribe(Id, fact => { if (fact.Kind == LifecycleEventKind.SessionInvalidated) ClearTargets(); }));
            if (panel == null || !panel.Capabilities.ContextualActions || commands == null || tactics == null || settlement == null)
            { Logger.LogWarning("Content registered; optional contextual control/settlement services unavailable."); return; }
            _leases.Add(panel.RegisterAction(Id, "attach-cargo", view => view.Operation == null
                ? new DungeonPanelAction("Attach cargo encounter", "Explicitly attach cargo content to this observed target. Existing attachments are never replaced.") : null,
                view => Logger.LogInfo("Cargo attach: " + _author.Attach(view.Target.Handle).Status)));
            void Track(BoardingOperationSnapshot operation)
            {
                if (_panels.ContainsKey(operation.Target)) return;
                _panels.Add(operation.Target, new CargoRecoveryPanel(Id, operation.Target, panel, boarding, commands, tactics, settlement,
                    result => Logger.LogInfo("Cargo command: " + result.Status),
                    result => Logger.LogInfo($"Cargo settlement: {result.NativeOutcome}; crew return settled={result.CrewReturnSettled}; observed counts={result.CrewCountsObserved}")));
            }
            _leases.Add(boarding.Subscribe(Id, fact => { if (fact.Operation != null) Track(fact.Operation); }));
            foreach (var operation in boarding.GetOperations()) Track(operation);
        }
        catch (Exception error) { OnDestroy(); Logger.LogError(error); }
    }
    private void ClearTargets()
    { foreach (var panel in _panels.Values.ToArray()) panel.Dispose(); _panels.Clear(); }
    private void OnDestroy()
    {
        ClearTargets();
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear(); _author?.Dispose(); _author = null;
    }
}

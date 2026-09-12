using System;
using System.Collections.Generic;
using VGModAPI;

namespace CargoRecovery;

/// <summary>Optional, per-target controls layered onto <see cref="CargoEncounter"/> using only public contracts.</summary>
public sealed class CargoEncounterPanel : IDisposable
{
    private readonly List<IDisposable> _leases = new();
    private IDungeonOperationService? _boarding;
    private Action<BoardingEvent>? _boardingHandler;
    private IDungeonSettlementService? _settlement;
    private Action<DungeonSettlementSnapshot>? _settlementHandler;

    public CargoEncounterPanel(string pluginId, BoardingHandle target, IDungeonPanelService panel,
        IDungeonOperationService boarding, IDungeonCommandService commands, IDungeonTacticalService tactics, IDungeonSettlementService settlement,
        Action<BoardingCommandResult> commandResult, Action<DungeonSettlementSnapshot> observedSettlement)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        if (boarding == null) throw new ArgumentNullException(nameof(boarding));
        if (commands == null) throw new ArgumentNullException(nameof(commands));
        if (tactics == null) throw new ArgumentNullException(nameof(tactics));
        if (settlement == null) throw new ArgumentNullException(nameof(settlement));
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (commandResult == null) throw new ArgumentNullException(nameof(commandResult));
        if (observedSettlement == null) throw new ArgumentNullException(nameof(observedSettlement));
        var operations = new HashSet<BoardingHandle>();
        try
        {
            // Observe independently of presentation: closing the panel must not lose returning-crew facts.
            _boarding = boarding;
            _boardingHandler = fact =>
            {
                if (fact.Target.Handle.Equals(target) && fact.Operation != null) operations.Add(fact.Operation.Handle);
            };
            boarding.Changed += _boardingHandler;
            foreach (var operation in boarding.GetOperations())
                if (operation.Target.Equals(target)) operations.Add(operation.Handle);
            _settlement = settlement;
            _settlementHandler = snapshot =>
            {
                // Settlement may publish before our boarding subscription sees the introducing event.
                if (boarding.GetOperation(snapshot.Operation)?.Target.Equals(target) == true) operations.Add(snapshot.Operation);
                if (operations.Contains(snapshot.Operation)) observedSettlement(snapshot);
            };
            settlement.Changed += _settlementHandler;
            _leases.Add(panel.RegisterAction(pluginId, "cargo-extraction-" + target.Generation.ToString("N"), view =>
            {
                if (!view.Target.Handle.Equals(target) || view.Operation == null) return null;
                var state = tactics.GetSnapshot(view.Operation.Handle);
                if (state == null || !state.CanRequestExtraction || state.AwaitingExtraction) return null;
                return new DungeonPanelAction("Request extraction",
                    "Requests extraction only. Confirmation, crew arrival and settlement remain separate.");
            }, view =>
            {
                if (!view.Target.Handle.Equals(target) || view.Operation == null) return;
                // Do not hold control merely because a panel is visible. Another mod may own it.
                var acquired = commands.AcquireControl(pluginId, target, out var controller);
                if (!acquired.Admitted || controller == null) { controller?.Dispose(); commandResult(acquired); return; }
                using (controller)
                    commandResult(tactics.Execute(controller, new BoardingTacticalRequest(BoardingTacticalAction.RequestExtraction)));
            }));
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_settlement != null) _settlement.Changed -= _settlementHandler;
        _settlement = null; _settlementHandler = null;
        if (_boarding != null) _boarding.Changed -= _boardingHandler;
        _boarding = null; _boardingHandler = null;
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear();
    }
}

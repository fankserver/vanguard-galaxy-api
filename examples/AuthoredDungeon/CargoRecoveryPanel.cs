using System;
using System.Collections.Generic;
using VGModAPI;

namespace AuthoredDungeon;

/// <summary>Optional, per-target controls layered onto CargoRecovery using only public contracts.</summary>
public sealed class CargoRecoveryPanel : IDisposable
{
    private readonly List<IDisposable> _leases = new();

    public CargoRecoveryPanel(string pluginId, BoardingHandle target, IDungeonPanelApi panel,
        IBoardingEvents boarding, IBoardingCommands commands, IBoardingTactics tactics, IDungeonSettlement settlement,
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
            _leases.Add(boarding.Subscribe(pluginId, fact =>
            {
                if (fact.Target.Handle.Equals(target) && fact.Operation != null) operations.Add(fact.Operation.Handle);
            }));
            foreach (var operation in boarding.GetOperations())
                if (operation.Target.Equals(target)) operations.Add(operation.Handle);
            _leases.Add(settlement.Subscribe(pluginId, snapshot =>
            {
                // Settlement may publish before our boarding subscription sees the introducing event.
                if (boarding.GetOperation(snapshot.Operation)?.Target.Equals(target) == true) operations.Add(snapshot.Operation);
                if (operations.Contains(snapshot.Operation)) observedSettlement(snapshot);
            }));
            _leases.Add(panel.RegisterAction(pluginId, "cargo-extraction", view =>
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
        for (var i = _leases.Count - 1; i >= 0; i--) _leases[i].Dispose();
        _leases.Clear();
    }
}

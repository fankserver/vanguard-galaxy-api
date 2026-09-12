using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>
/// The public dungeon service fronts the whole boarding/dungeon domain on one entry point, matching
/// the game's single <c>DungeonManager</c>: combat, rewards, commands, tactics, operations,
/// settlement and the panel are all the same runtime, not eight separately-injected contracts. This
/// facade implements <see cref="IDungeonService"/> by delegating to the internal per-facet services,
/// which keep their existing save-safety and native-boundary machinery untouched.
/// </summary>
internal sealed class DungeonFacade : IDungeonService, IDisposable
{
    private readonly DungeonService _dungeons;
    private readonly BoardingCombatService _combat;
    private readonly DungeonRewardService _rewards;
    private readonly BoardingCommandService _commands;
    private readonly Runtime.BoardingTacticalAdapter _tactics;
    private readonly BoardingService _operations;
    private readonly DungeonSettlementService _settlement;
    private readonly DungeonPanelService _panel;

    internal DungeonFacade(DungeonService dungeons, BoardingCombatService combat, DungeonRewardService rewards,
        BoardingCommandService commands, Runtime.BoardingTacticalAdapter tactics, BoardingService operations,
        DungeonSettlementService settlement, DungeonPanelService panel)
    {
        _dungeons = dungeons; _combat = combat; _rewards = rewards; _commands = commands;
        _tactics = tactics; _operations = operations; _settlement = settlement; _panel = panel;
    }

    public ServiceAvailability Availability => _dungeons.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _dungeons.AvailabilityChanged += value; remove => _dungeons.AvailabilityChanged -= value; }

    public IDungeonProvider AcquireProvider(string pluginId, ISaveDataRegistration? saveData = null)
        => _dungeons.AcquireProvider(pluginId, saveData);

    public bool IsEvaluating => _combat.IsEvaluating;
    public IBoardingCombatProvider AcquireCombatProvider(string pluginId) => _combat.AcquireProvider(pluginId);
    public IDungeonRewardProvider AcquireRewardProvider(string pluginId) => _rewards.AcquireProvider(pluginId);

    public Guid? SessionId => _operations.SessionId;
    public IReadOnlyList<BoardingTargetSnapshot> GetTargets() => _operations.GetTargets();
    public IReadOnlyList<BoardingOperationSnapshot> GetOperations() => _operations.GetOperations();
    public BoardingTargetSnapshot? GetTarget(BoardingHandle handle) => _operations.GetTarget(handle);
    public BoardingOperationSnapshot? GetOperation(BoardingHandle handle) => _operations.GetOperation(handle);
    public event Action<BoardingEvent>? Changed
    { add => _operations.Changed += value; remove => _operations.Changed -= value; }

    public BoardingCommandResult AcquireControl(string pluginId, BoardingHandle target, out IBoardingController? controller)
        => _commands.AcquireControl(pluginId, target, out controller);

    public BoardingTacticalSnapshot? GetSnapshot(BoardingHandle operation) => _tactics.GetSnapshot(operation);
    public BoardingCommandResult Execute(IBoardingController controller, BoardingTacticalRequest request)
        => _tactics.Execute(controller, request);

    public DungeonSettlementSnapshot? Get(BoardingHandle operation) => _settlement.Get(operation);
    public event Action<DungeonSettlementSnapshot>? SettlementChanged
    { add => _settlement.Changed += value; remove => _settlement.Changed -= value; }

    public DungeonPanelCapabilities Capabilities => _panel.Capabilities;
    public DungeonPanelSnapshot? Current => _panel.Current;
    public DungeonPanelOpenStatus Open(BoardingHandle target) => _panel.Open(target);
    public IDisposable RegisterSection(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelSection?> present, int order = 0)
        => _panel.RegisterSection(pluginId, localId, present, order);
    public IDisposable RegisterAction(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelAction?> present,
        Action<DungeonPanelSnapshot> activate, int order = 0)
        => _panel.RegisterAction(pluginId, localId, present, activate, order);

    public void Dispose()
    {
        _dungeons.Dispose(); _combat.Dispose(); _rewards.Dispose(); _commands.Dispose();
        _tactics.Dispose(); _operations.Dispose(); _settlement.Dispose(); _panel.Dispose();
    }
}

using System;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonRewardAdapter
{
    private readonly LifecycleHub _hub;
    private readonly BoardingObserver _observer;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonRewardScopes _scopes;
    internal DungeonRewardAdapter(LifecycleHub hub, GameBindings game, BoardingObserver observer, DungeonRewardService rules)
    {
        _hub = hub; _observer = observer; _scopes = new(rules);
        _native = new BoardingCommandNativeBindings(game, DungeonSettlementBindings.Hooks, DungeonSettlementBindings.Members);
    }
    internal IDisposable? Begin(object operation, object? lootEntry, bool mastery)
    {
        _hub.CheckThread(); var handle = _observer.CommandHandleForOperation(operation); if (handle == null) return null;
        var location = _native.Get(operation, "location");
        var outcome = _native.Get(_native.Get(operation, "simulation"), "outcome")?.ToString() ?? "Unknown";
        var mission = !string.IsNullOrEmpty((string?)_native.Get(location, "settlementToken")) ||
            !string.IsNullOrEmpty((string?)_native.Get(_native.Get(location, "shipData"), "settlementMissionGuid"));
        var recipient = mastery ? _native.Get(_native.Get(operation, "operationShip"), "settlementShipData") : lootEntry;
        return _scopes.Begin(handle, mastery ? DungeonRewardKind.MasteryExperience : DungeonRewardKind.LootAmount, outcome, mission, recipient);
    }
    internal int LootCount(object entry, int nativeAmount) { _hub.CheckThread(); return _scopes.LootAmount(entry, nativeAmount); }
    internal float Mastery(object recipient, float nativeAmount, string specialization)
    { _hub.CheckThread(); return specialization == "Leadership" ? _scopes.Mastery(recipient, nativeAmount) : nativeAmount; }
}

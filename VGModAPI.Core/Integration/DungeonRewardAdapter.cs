using System;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonRewardAdapter
{
    private readonly LifecycleHub _hub;
    private readonly Func<object, BoardingHandle?> _operationHandle;
    private readonly ConditionalWeakTable<object, Protection> _protection = new();
    private sealed class Protection
    {
        internal readonly BoardingHandle Handle;
        internal bool Mission;
        internal Protection(BoardingHandle handle) { Handle = handle; }
    }
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonRewardScopes _scopes;
    internal DungeonRewardAdapter(LifecycleHub hub, GameBindings game, BoardingObserver observer, DungeonRewardService rules)
        : this(hub, observer.CommandHandleForOperation, rules, new BoardingCommandNativeBindings(game, DungeonSettlementBindings.Hooks, DungeonSettlementBindings.Members)) { }
    internal DungeonRewardAdapter(LifecycleHub hub, Func<object, BoardingHandle?> operationHandle, DungeonRewardService rules, IBoardingTacticalNativeBindings native)
    { _hub = hub; _operationHandle = operationHandle; _scopes = new(rules); _native = native; }
    internal void RetainProtection(object operation)
    {
        _hub.CheckThread(); var handle = _operationHandle(operation); if (handle == null) return;
        if (!_protection.TryGetValue(operation, out var protection) || !protection.Handle.Equals(handle))
        {
            _protection.Remove(operation); protection = new(handle); _protection.Add(operation, protection);
        }
        var location = _native.Get(operation, "location");
        protection.Mission |= !string.IsNullOrEmpty((string?)_native.Get(location, "settlementToken")) ||
            !string.IsNullOrEmpty((string?)_native.Get(_native.Get(location, "shipData"), "settlementMissionGuid"));
    }
    internal IDisposable? Begin(object operation, object? lootEntry, bool mastery)
    {
        _hub.CheckThread(); var handle = _operationHandle(operation); if (handle == null) return _scopes.Mask();
        RetainProtection(operation);
        var outcome = _native.Get(_native.Get(operation, "simulation"), "outcome")?.ToString() ?? "Unknown";
        var mission = _protection.TryGetValue(operation, out var protection) && protection.Mission;
        var recipient = mastery ? _native.Get(_native.Get(operation, "operationShip"), "settlementShipData") : lootEntry;
        return _scopes.Begin(handle, mastery ? DungeonRewardKind.MasteryExperience : DungeonRewardKind.LootAmount, outcome, mission, recipient);
    }
    internal int LootCount(object entry, int nativeAmount) { _hub.CheckThread(); return _scopes.LootAmount(entry, nativeAmount); }
    internal float Mastery(object recipient, float nativeAmount, string specialization)
    { _hub.CheckThread(); return specialization == "Leadership" ? _scopes.Mastery(recipient, nativeAmount) : nativeAmount; }
}

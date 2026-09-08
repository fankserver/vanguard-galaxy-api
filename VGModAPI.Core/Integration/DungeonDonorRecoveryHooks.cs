using System;
using System.Runtime.CompilerServices;
using VGModAPI.Core;
namespace VGModAPI.Runtime;

internal sealed class DungeonDonorRecoveryHooks
{
    private readonly DungeonPodPersistence _state;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Func<object?, bool> _targetAlive;
    private readonly Func<object, bool> _ready;
    private readonly Action<Exception> _report;
    private ConditionalWeakTable<object, Ownership> _owners = new();
    private sealed class Ownership
    {
        internal readonly Guid Operation; internal readonly string ShipId; internal readonly object Ship, Token;
        internal Ownership(Guid operation, string shipId, object ship, object token) { Operation = operation; ShipId = shipId; Ship = ship; Token = token; }
    }
    internal DungeonDonorRecoveryHooks(DungeonPodPersistence state, IBoardingTacticalNativeBindings native, Func<object?, bool> targetAlive, Func<object, bool> ready, Action<Exception> report)
    { _state = state; _native = native; _targetAlive = targetAlive; _ready = ready; _report = report; }
    internal void Capture(object actions, Guid operation, string shipId, object ship)
    { _owners.Remove(actions); _owners.Add(actions, new(operation, shipId, ship, _state.RestoreToken)); }
    internal void Clear() => _owners = new();
    internal bool BeginDonorUpdate(object actions, out DungeonMutationFence.Lease? abort)
    {
        abort = null;
        if (_native.Get(actions, "donorDispatched") is true) return true;
        if (!_targetAlive(_native.Get(actions, "donorTarget"))) { abort = _state.BeginTransfer(); return abort != null; }
        return _ready(actions);
    }
    internal void DonorAborted(object actions)
    {
        try
        {
            if (!_owners.TryGetValue(actions, out var owner) || !ReferenceEquals(owner.Token, _state.RestoreToken)) return;
            if (ReferenceEquals(_native.Get(owner.Ship, "donorActions"), actions)) return;
            if (!_state.CompleteDonorAbort(owner.Operation, owner.ShipId, owner.Token)) _state.RejectTransferSnapshot();
            _owners.Remove(actions);
        }
        catch (Exception error) { try { _state.RejectTransferSnapshot(); } catch { } try { _report(error); } catch { } }
    }
}

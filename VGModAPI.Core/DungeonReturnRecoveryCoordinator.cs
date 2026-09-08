using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal interface IDungeonReturnInstance : IDisposable
{
    bool Alive { get; }
    void Activate();
}

/// <summary>Resolves exact recipients and binds guards before activating reconstructed return pods.</summary>
internal sealed class DungeonReturnRecoveryCoordinator : IDisposable
{
    private readonly DungeonPodPersistence _state;
    private readonly Func<string, object?> _recipient;
    private readonly Func<DungeonPodResumeState, DungeonOperationResumeState, object, IDungeonReturnInstance> _build;
    private readonly Action<IDungeonReturnInstance, Guid> _bind;
    private readonly Action<Exception> _report;
    private readonly Func<DungeonPodResumeState, bool> _eligible;
    private readonly Dictionary<Guid, IDungeonReturnInstance> _live = new();
    private readonly HashSet<Guid> _attempted = new();
    private object? _token;
    private bool _disposed, _polling;
    internal DungeonReturnRecoveryCoordinator(DungeonPodPersistence state, Func<string, object?> recipient,
        Func<DungeonPodResumeState, DungeonOperationResumeState, object, IDungeonReturnInstance> build,
        Action<IDungeonReturnInstance, Guid> bind, Action<Exception> report, Func<DungeonPodResumeState, bool>? eligible = null)
    { _eligible = eligible ?? (_ => true); _state = state; _recipient = recipient; _build = build; _bind = bind; _report = report; }
    internal void Poll()
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            if (!ReferenceEquals(_token, _state.RestoreToken)) { Clear(); _token = _state.RestoreToken; }
            if (!_state.CanMutate) return;
            foreach (var pod in _state.Snapshot)
            {
                if (!_state.WasRestored(pod.Id) || !pod.CanRecover || pod.Transport == null || _attempted.Contains(pod.Id)) continue;
                var operation = _state.Operation(pod.OperationId); if (operation == null || !_eligible(pod)) continue;
                var recipient = _recipient(pod.ParentShipId); if (recipient == null) continue;
                _attempted.Add(pod.Id); IDungeonReturnInstance? instance = null;
                try
                {
                    instance = _build(pod, operation, recipient);
                    if (!ReferenceEquals(_token, _state.RestoreToken) || !_state.CanMutate) { instance.Dispose(); break; }
                    _bind(instance, pod.Id); _live.Add(pod.Id, instance); instance.Activate();
                }
                catch (Exception error)
                {
                    _live.Remove(pod.Id); try { instance?.Dispose(); } catch { }
                    try { _report(error); } catch { }
                }
            }
        }
        finally { _polling = false; }
    }
    internal void MarkLive(Guid id)
    {
        if (!ReferenceEquals(_token, _state.RestoreToken)) { Clear(); _token = _state.RestoreToken; }
        _attempted.Add(id);
    }
    private void Clear()
    {
        foreach (var instance in _live.Values) { try { instance.Dispose(); } catch (Exception error) { try { _report(error); } catch { } } }
        _live.Clear(); _attempted.Clear();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }
}

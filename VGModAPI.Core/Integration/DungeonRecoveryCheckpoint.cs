using System;
using System.Collections;
using VGModAPI.Core;
namespace VGModAPI.Runtime;

internal sealed class DungeonRecoveryCheckpoint
{
    private readonly DungeonPodPersistence _state;
    private readonly Func<IEnumerable?> _operations;
    private readonly Func<object, bool> _observe;
    private readonly Action _validateReturns;
    private readonly DungeonLiveTransportIndex _live;
    private readonly Action<Guid, object> _captureTransport;
    internal DungeonRecoveryCheckpoint(DungeonPodPersistence state, Func<IEnumerable?> operations, Func<object, bool> observe,
        Action validateReturns, DungeonLiveTransportIndex live, Action<Guid, object> captureTransport)
    { _state = state; _operations = operations; _observe = observe; _validateReturns = validateReturns; _live = live; _captureTransport = captureTransport; }
    internal void Run()
    {
        _state.EnsureSerializationAllowed();
        if (_state.Ready && _operations() is { } operations)
            _state.Checkpoint(() =>
            {
                foreach (var operation in operations)
                    if (!_observe(operation)) throw new InvalidOperationException("Cannot checkpoint unresolved native operation.");
            });
        _validateReturns(); _live.Checkpoint(_captureTransport);
    }
}

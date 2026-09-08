using System;
using VGModAPI.Core;
namespace VGModAPI.Runtime;

/// <summary>Native refund boundary dependencies, shared by runtime patches and host integration tests.</summary>
internal sealed class DungeonRefundHooks
{
    internal DungeonPodPersistence State { get; }
    internal DungeonPodReturnObserver ReturnObserver { get; }
    internal Func<object, bool> ObserveOperation { get; }
    internal Func<object, Guid?> OperationId { get; }
    internal DungeonRefundHooks(DungeonPodPersistence state, DungeonPodReturnObserver observer, Func<object, bool> observe, Func<object, Guid?> identity)
    { State = state; ReturnObserver = observer; ObserveOperation = observe; OperationId = identity; }
}

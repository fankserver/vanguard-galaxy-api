using System;
using System.Threading;

namespace VGModAPI.Core;

/// <summary>Matches a native pickup float to its synchronous item context without leaking across calls.</summary>
internal sealed class PickupPresentationScope
{
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private Frame? _frame;
    internal bool IsOwnerThread => Thread.CurrentThread.ManagedThreadId == _thread;
    internal Frame? Begin()
    {
        if (!IsOwnerThread) return null;
        return _frame = new Frame(this, _frame);
    }
    internal ItemPickupPresentation? Consume(bool isPickup, string postfix)
    {
        if (!IsOwnerThread || !isPickup || _frame?.Pickup is not ItemPickupPresentation pickup || pickup.DisplayName != postfix)
            return null;
        _frame.Pickup = null;
        return pickup;
    }
    internal sealed class Frame : IDisposable
    {
        private readonly PickupPresentationScope _owner;
        private readonly Frame? _previous;
        private bool _disposed;
        internal ItemPickupPresentation? Pickup;
        internal Frame(PickupPresentationScope owner, Frame? previous) { _owner = owner; _previous = previous; }
        public void Dispose()
        {
            if (!_owner.IsOwnerThread || _disposed) return;
            _disposed = true;
            _owner._frame = _previous;
        }
    }
}

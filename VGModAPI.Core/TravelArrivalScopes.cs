using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

// Coalesce nested base/override calls for one manager, not independent managers.
// Readiness, player identity and leg ownership are still adapter obligations.
internal sealed class TravelArrivalScopes
{
    internal sealed class Scope
    {
        internal readonly object Manager;
        internal readonly long Epoch;
        internal readonly Scope? Parent;
        internal bool Failed, Closed;
        internal Scope(object manager, long epoch, Scope? parent) { Manager = manager; Epoch = epoch; Parent = parent; }
    }
    private readonly Stack<Scope> _stack = new();
    private long _epoch;
    internal Scope Begin(object manager)
    {
        if (manager == null) throw new ArgumentNullException(nameof(manager));
        var scope = new Scope(manager, _epoch, _stack.Count == 0 ? null : _stack.Peek());
        _stack.Push(scope); return scope;
    }
    internal bool End(Scope scope, Exception? error)
    {
        if (scope.Closed || scope.Epoch != _epoch) return false;
        if (_stack.Count == 0 || !ReferenceEquals(_stack.Peek(), scope))
            throw new InvalidOperationException("Arrival scopes must close in nesting order.");
        scope.Closed = true; _stack.Pop(); scope.Failed |= error != null;
        // An intervening different manager does not erase the outer call for this
        // manager. Suppress its reentrant duplicate until that outer call closes.
        for (var parent = scope.Parent; parent != null; parent = parent.Parent)
            if (ReferenceEquals(parent.Manager, scope.Manager))
            {
                parent.Failed |= scope.Failed;
                return false;
            }
        return !scope.Failed;
    }
    internal void Reset() { _epoch++; _stack.Clear(); }
}

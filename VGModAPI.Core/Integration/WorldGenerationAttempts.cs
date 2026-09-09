using System;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Counts guarded builder entries, not native loop iterations or work inside a builder.</summary>
internal sealed class WorldGenerationAttempts
{
    internal sealed class Budget { internal int Remaining; internal Budget(int limit) => Remaining = limit; }
    internal sealed class Scope
    {
        private readonly WorldGenerationAttempts _owner;
        internal readonly object _epoch;
        internal readonly Scope? _parent;
        internal readonly Func<bool>? _valid;
        internal readonly Budget? _budget;
        internal bool _closed;
        internal Scope(WorldGenerationAttempts owner, object epoch, Scope? parent, Func<bool>? valid, Budget? budget)
        { _owner = owner; _epoch = epoch; _parent = parent; _valid = valid; _budget = budget; }
        internal Exception? Finish(Exception? error)
        {
            try
            {
                if (_closed) return error;
                if (_budget != null)
                {
                    if (error != null) _owner.Reject(_epoch);
                    else _owner.Require(this);
                }
                return error;
            }
            catch (Exception refusal) { return error ?? refusal; }
            finally
            {
                _closed = true;
                if (ReferenceEquals(_owner._current, this))
                {
                    var parent = _parent;
                    while (parent != null && parent._closed) parent = parent._parent;
                    _owner._current = parent;
                }
            }
        }
    }
    private object _epoch = new();
    private Scope? _current;
    private bool _failed, _checking;
    private readonly int _limit;
    internal bool Failed => _failed;
    internal WorldGenerationAttempts(int limit)
    { if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit)); _limit = limit; }
    internal void Reset() { _epoch = new(); _failed = false; _checking = false; }
    private void Reject(object epoch) { if (ReferenceEquals(epoch, _epoch)) _failed = true; }
    internal Scope Begin(Func<bool>? valid)
    {
        Budget? budget = null;
        if (valid != null)
        {
            if (_failed || _checking) { _failed = true; throw new InvalidDataException("Owned generation is rejected or reentrant."); }
            for (var parent = _current; parent != null && ReferenceEquals(parent._epoch, _epoch); parent = parent._parent)
                if (parent._budget != null) { budget = parent._budget; break; }
            budget ??= new Budget(_limit);
        }
        var scope = new Scope(this, _epoch, _current, valid, budget); _current = scope;
        try { if (valid != null) Require(scope); return scope; }
        catch (Exception error) { scope.Finish(error); throw; }
    }
    private void Require(Scope scope)
    {
        var epoch = scope._epoch;
        try
        {
            if (!ReferenceEquals(epoch, _epoch) || !ReferenceEquals(scope, _current) || scope._closed || _failed || _checking)
                throw new InvalidDataException("Stale or rejected owned generation attempt.");
            _checking = true;
            for (var frame = scope; frame != null && ReferenceEquals(frame._epoch, epoch); frame = frame._parent)
            {
                if (!frame._closed && frame._valid != null && !frame._valid()) throw new InvalidDataException("Owned generation origin is unavailable.");
                if (!ReferenceEquals(epoch, _epoch) || !ReferenceEquals(scope, _current) || _failed)
                    throw new InvalidDataException("Owned generation changed during admission.");
            }
        }
        catch { Reject(epoch); throw; }
        finally { if (ReferenceEquals(epoch, _epoch)) _checking = false; }
    }
    internal void ValidateCurrent()
    {
        var scope = _current;
        if (scope?._budget != null) Require(scope);
    }
    internal void Consume()
    {
        var scope = _current;
        if (scope?._budget == null) return;
        Require(scope);
        if (scope._budget.Remaining == 0) { Reject(scope._epoch); throw new InvalidDataException("Owned generation builder-entry budget exhausted."); }
        scope._budget.Remaining--;
    }
}

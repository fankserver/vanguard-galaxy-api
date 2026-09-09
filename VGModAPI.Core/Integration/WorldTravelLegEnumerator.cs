using System;
using System.Collections;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Leg-root iteration with explicit handoff retirement instead of executing the predecessor's shared-state tail.</summary>
internal sealed class WorldTravelLegEnumerator : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly WorldTravelScopes _scopes;
    private readonly WorldTravelScopes.Leg _leg;
    private readonly Action _verifyNative;
    private readonly bool _root;
    private bool _ended, _disposed;
    public object? Current { get; private set; }
    internal WorldTravelLegEnumerator(IEnumerator inner, WorldTravelScopes scopes, WorldTravelScopes.Leg leg, Action verifyNative)
        : this(inner, scopes, leg, verifyNative, true) { }
    private WorldTravelLegEnumerator(IEnumerator inner, WorldTravelScopes scopes, WorldTravelScopes.Leg leg, Action verifyNative, bool root)
    { _inner = inner; _scopes = scopes; _leg = leg; _verifyNative = verifyNative; _root = root; }
    private bool HandedOff => _leg.HandedOff && _scopes.IsCurrent(_leg.Route);
    private bool Retire()
    {
        if (_root) _scopes.RetirePredecessor(_leg);
        _ended = true; Current = null; return false;
    }
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        try
        {
            if (HandedOff) return Retire();
            _verifyNative();
            using var execution = _scopes.Enter(_leg);
            bool moved = _inner.MoveNext();
            if (HandedOff)
            {
                if (moved) throw new InvalidDataException("Predecessor yielded more work after waypoint handoff.");
                return Retire();
            }
            _verifyNative(); _ = _scopes.CaptureExecuting();
            if (!moved)
            {
                if (_root) _scopes.Complete(_leg);
                _ended = true; Current = null; return false;
            }
            var value = _inner.Current;
            _verifyNative(); _ = _scopes.CaptureExecuting();
            Current = value is IEnumerator child ? new WorldTravelLegEnumerator(child, _scopes, _leg, _verifyNative, false) : value;
            return true;
        }
        catch { _ended = true; Current = null; _scopes.Cancel(_leg); throw; }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ended = true; Current = null;
        using var cleanup = _scopes.EnterCleanup(_leg);
        (_inner as IDisposable)?.Dispose();
    }
}

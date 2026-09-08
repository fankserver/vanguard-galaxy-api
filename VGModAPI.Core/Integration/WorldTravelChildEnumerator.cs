using System;
using System.Collections;

namespace VGModAPI.Core.Integration;

/// <summary>Executes independently scheduled child work under its captured leg, including nested waits.</summary>
internal sealed class WorldTravelChildEnumerator : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly WorldTravelScopes _scopes;
    private readonly WorldTravelScopes.Leg _leg;
    private readonly Action _verifyNative;
    private bool _ended, _disposed;
    public object? Current { get; private set; }
    internal WorldTravelChildEnumerator(IEnumerator inner, WorldTravelScopes scopes, WorldTravelScopes.Leg leg, Action verifyNative)
    { _inner = inner; _scopes = scopes; _leg = leg; _verifyNative = verifyNative; }
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        try
        {
            _verifyNative();
            using var execution = _scopes.Enter(_leg);
            bool moved = _inner.MoveNext();
            _verifyNative();
            _ = _scopes.CaptureExecuting();
            if (!moved) { _ended = true; Current = null; return false; }
            var value = _inner.Current;
            _verifyNative(); _ = _scopes.CaptureExecuting();
            Current = value is IEnumerator child ? new WorldTravelChildEnumerator(child, _scopes, _leg, _verifyNative) : value;
            return true;
        }
        catch { _ended = true; Current = null; _scopes.Cancel(_leg); throw; }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ended = true; Current = null;
        // Stale cleanup cannot accidentally inherit a successor's active execution frame.
        using var cleanup = _scopes.EnterCleanup(_leg);
        (_inner as IDisposable)?.Dispose();
    }
}

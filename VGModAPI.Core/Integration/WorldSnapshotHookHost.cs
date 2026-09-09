using System;
using System.Collections.Generic;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Lifecycle-scoped snapshot and nested Store contexts. Does not infer successful disk publication.</summary>
internal sealed class WorldSnapshotHookHost : IDisposable
{
    private sealed class Capture
    {
        internal readonly WorldSnapshotHookHost Host;
        internal readonly Guid Session;
        internal readonly long Revision;
        internal readonly object Token;
        internal Capture(WorldSnapshotHookHost host, Guid session, long revision, object token) { Host = host; Session = session; Revision = revision; Token = token; }
    }
    private sealed class StoreScope : IDisposable
    {
        internal readonly WorldSnapshotHookHost Host;
        internal readonly long Epoch;
        internal readonly Guid Session;
        internal readonly Dictionary<string, byte[]> Payload;
        internal bool Closed;
        internal StoreScope(WorldSnapshotHookHost host, long epoch, Guid session, Dictionary<string, byte[]> payload)
        { Host = host; Epoch = epoch; Session = session; Payload = payload; }
        public void Dispose() => Host.EndStore(this);
    }
    private readonly LifecycleHub _hub;
    private readonly WorldSnapshotRecorder _recorder;
    private readonly Func<IReadOnlyList<WorldSnapshotInstance>> _instances;
    private readonly Func<long> _revision;
    private readonly Action? _requireContext;
    private readonly IDisposable _subscription;
    private readonly Stack<StoreScope> _stores = new();
    private long _epoch;
    private Guid _session;
    private bool _disposed;
    internal WorldSnapshotHookHost(LifecycleHub hub, WorldSnapshotRecorder recorder,
        Func<IReadOnlyList<WorldSnapshotInstance>> instances, Func<long> revision, Action? requireContext = null)
    {
        _hub = hub; _recorder = recorder; _instances = instances; _revision = revision; _requireContext = requireContext;
        _hub.CheckThread();
        if (_hub.CurrentSession != null) throw new InvalidOperationException("World snapshots must attach before a session.");
        _subscription = hub.Subscribe("vgmodapi.world-snapshots", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
            { Reset(); _session = e.Session!.Id; }
            else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session)
            { Reset(); _session = Guid.Empty; }
        });
    }
    private Guid Session()
    {
        _hub.CheckThread();
        // Fixed state inspector only; not an extension callback. Store receipts remain
        // immutable, but capture-time trust must not authorize use after context loss.
        _requireContext?.Invoke();
        if (_disposed || _hub.CurrentSession is not { } session || session.Id != _session ||
            (session.Phase != SessionPhase.PlayerReady && session.Phase != SessionPhase.GameplayInitialized))
            throw new InvalidDataException("World snapshot requires a current player-ready session.");
        return session.Id;
    }
    private void Require(Guid session, long revision)
    {
        // Provider/source delegates may invalidate the session; check lifecycle after their return.
        if (_revision() != revision || Session() != session) throw new InvalidDataException("World snapshot scope changed.");
    }
    internal object BeginSnapshot()
    {
        Guid session = Session(); long revision = _revision();
        var instances = _instances();
        Require(session, revision);
        var token = _recorder.Begin(revision, instances);
        Require(session, revision);
        return new Capture(this, session, revision, token);
    }
    internal void CompleteSnapshot(object token, object root)
    {
        _hub.CheckThread();
        if (token is not Capture capture || !ReferenceEquals(capture.Host, this)) throw new InvalidDataException("Unknown world snapshot capture.");
        Require(capture.Session, capture.Revision);
        var instances = _instances();
        Require(capture.Session, capture.Revision);
        bool complete = _recorder.Complete(capture.Token, capture.Revision, instances, root,
            () => Require(capture.Session, capture.Revision));
        if (!complete) throw new InvalidDataException("Native world snapshot could not be associated.");
    }
    internal IDisposable BeginStore(object root)
    {
        Guid session = Session(); long epoch = _epoch;
        var payload = _recorder.ForStore(root);
        if (Session() != session || epoch != _epoch) throw new InvalidDataException("World Store scope changed.");
        var scope = new StoreScope(this, epoch, session, payload); _stores.Push(scope); return scope;
    }
    internal byte[] CaptureOwner(string owner)
    {
        Guid session = Session();
        if (_stores.Count == 0 || _stores.Peek() is not { } scope || scope.Closed || scope.Epoch != _epoch || scope.Session != session ||
            !scope.Payload.TryGetValue(owner, out var bytes)) throw new InvalidDataException("No current world Store association.");
        return (byte[])bytes.Clone();
    }
    private void EndStore(StoreScope scope)
    {
        _hub.CheckThread();
        if (scope.Closed) return;
        if (scope.Epoch != _epoch || !_stores.Contains(scope)) { scope.Closed = true; return; }
        while (_stores.Count > 0)
        {
            var ended = _stores.Pop(); ended.Closed = true;
            if (ReferenceEquals(ended, scope)) break;
        }
    }
    private void Reset() { _epoch = checked(_epoch + 1); _stores.Clear(); _recorder.Reset(); }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; Reset(); _subscription.Dispose();
    }
}

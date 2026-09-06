using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace VGModAPI.Core;

internal readonly struct MissionFacts
{
    internal bool BeforeActive { get; }
    internal bool AfterActive { get; }
    internal bool BeforeFailed { get; }
    internal bool AfterFailed { get; }
    internal bool RewardRemovalObserved { get; }
    internal int ArchiveBefore { get; }
    internal int ArchiveAfter { get; }
    internal MissionFacts(bool BeforeActive, bool AfterActive, bool BeforeFailed = false, bool AfterFailed = false,
        bool RewardRemovalObserved = false, int ArchiveBefore = 0, int ArchiveAfter = 0)
    {
        this.BeforeActive = BeforeActive; this.AfterActive = AfterActive; this.BeforeFailed = BeforeFailed;
        this.AfterFailed = AfterFailed; this.RewardRemovalObserved = RewardRemovalObserved;
        this.ArchiveBefore = ArchiveBefore; this.ArchiveAfter = ArchiveAfter;
    }
}

internal sealed class MissionTransitions : IMissionEvents, IVersionSensitiveMissionAccess, IDisposable
{
    internal sealed class Observation : IDisposable
    {
        internal readonly MissionTransitions Owner;
        internal readonly long Epoch, Order;
        internal bool Closed;
        internal Observation(MissionTransitions owner, long epoch, long order) { Owner = owner; Epoch = epoch; Order = order; }
        public void Dispose() => Owner.End(this);
    }
    private sealed class Entry
    {
        internal readonly Guid Id;
        internal readonly MissionIdentityEvidence Evidence;
        internal Entry(Guid? id = null, MissionIdentityEvidence evidence = MissionIdentityEvidence.SessionOnly)
        { Id = id ?? Guid.NewGuid(); Evidence = evidence; }
        internal bool Accepted, Removed, SnapshotObserved;
        internal long RemovedOrder, EstablishedOrder;
        internal readonly HashSet<MissionTransitionKind> Seen = new();
    }
    private sealed class Subscription : IDisposable
    {
        internal readonly string Owner;
        internal readonly Action<MissionTransition> Callback;
        private readonly MissionTransitions _hub;
        internal bool Active = true;
        internal Subscription(MissionTransitions hub, string owner, Action<MissionTransition> callback) { _hub = hub; Owner = owner; Callback = callback; }
        public void Dispose() { _hub.CheckThread(); Active = false; _hub._subscriptions.Remove(this); }
    }
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string, Exception> _report;
    private ConditionalWeakTable<object, Entry> _entries = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Stack<Observation> _stack = new();
    private readonly List<(long order, long epoch, MissionTransitionKind kind, MissionSnapshot snapshot, object identity)> _pending = new();
    private Guid? _session;
    private long _epoch, _order, _sequence, _revision;
    internal long Revision { get { CheckThread(); return _revision; } }
    internal Guid SnapshotIdentity(object identity)
    {
        CheckThread();
        if (_disposed || !_session.HasValue) throw new InvalidOperationException("No active mission identity session.");
        if (!_entries.TryGetValue(identity, out var entry)) { entry = new Entry(); _entries.Add(identity, entry); }
        if (entry.Removed) throw new InvalidOperationException("Mission reappeared without an observed new occurrence.");
        if (!entry.SnapshotObserved && entry.Seen.Count == 0) entry.EstablishedOrder = ++_order;
        entry.SnapshotObserved = true;
        return entry.Id;
    }
    internal void SeedIdentity(object identity, Guid? id, MissionIdentityEvidence evidence)
    {
        CheckThread();
        if (_disposed || !_session.HasValue || id == Guid.Empty || !Enum.IsDefined(typeof(MissionIdentityEvidence), evidence) || (evidence == MissionIdentityEvidence.SavedSnapshotMatch) != id.HasValue)
            throw new InvalidOperationException("Invalid mission identity restoration.");
        if (_entries.TryGetValue(identity, out _)) throw new InvalidOperationException("Mission identity already established.");
        _entries.Add(identity, new Entry(id, evidence));
    }
    private bool _dispatching, _disposed;
    private MissionSnapshot? _dispatchSnapshot;
    private object? _dispatchIdentity;
    public bool TryGetNative(MissionSnapshot snapshot, out object? native)
    {
        CheckThread();
        native = !_disposed && ReferenceEquals(snapshot, _dispatchSnapshot) ? _dispatchIdentity : null;
        return native != null;
    }

    internal MissionTransitions(Action<string, Exception> report) { _report = report; }
    private void CheckThread()
    { if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Mission events are main-thread-only."); }
    internal void Reset(Guid? session)
    {
        CheckThread(); if (_disposed) return;
        if (session == Guid.Empty) throw new ArgumentException("Empty session.", nameof(session));
        _dispatchSnapshot = null; _dispatchIdentity = null;
        _revision++; _epoch++; _session = session; _entries = new(); _pending.Clear(); _stack.Clear();
    }
    internal bool WasRemoved(object identity)
    { CheckThread(); return _entries.TryGetValue(identity, out var entry) && entry.Removed; }
    internal bool WasRemovedSince(object identity, long order)
    { CheckThread(); return _entries.TryGetValue(identity, out var entry) && entry.Removed && entry.RemovedOrder > order; }
    internal bool HasActiveOccurrence(object identity)
    {
        CheckThread(); return _entries.TryGetValue(identity, out var entry) && !entry.Removed &&
            (entry.Seen.Contains(MissionTransitionKind.Accepted) || entry.Seen.Contains(MissionTransitionKind.Restored));
    }
    internal Observation Begin()
    {
        CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(MissionTransitions));
        var observation = new Observation(this, _epoch, ++_order); _stack.Push(observation); return observation;
    }
    internal void Record(Observation observation, object identity, MissionTransitionKind kind, MissionFacts facts,
        string? definitionId, string name, IEnumerable<string> tags)
    {
        CheckThread();
        if (observation.Owner != this) throw new ArgumentException("Foreign observation.");
        if (_disposed || observation.Closed || observation.Epoch != _epoch || !_session.HasValue) return;
        if (identity == null) throw new ArgumentNullException(nameof(identity));
        bool verified = kind switch
        {
            MissionTransitionKind.Restored => facts.AfterActive,
            MissionTransitionKind.Accepted => !facts.BeforeActive && facts.AfterActive,
            MissionTransitionKind.Completed => facts.BeforeActive && !facts.AfterActive && facts.RewardRemovalObserved,
            MissionTransitionKind.Failed => !facts.BeforeFailed && facts.AfterFailed,
            MissionTransitionKind.Abandoned => facts.BeforeActive && !facts.AfterActive && !facts.AfterFailed && !facts.RewardRemovalObserved,
            MissionTransitionKind.Removed => facts.BeforeActive && !facts.AfterActive,
            MissionTransitionKind.Archived => facts.ArchiveBefore >= 0 && facts.ArchiveAfter > facts.ArchiveBefore,
            _ => false
        };
        if (!verified) return;
        if (!_entries.TryGetValue(identity, out var entry) || (kind == MissionTransitionKind.Accepted &&
            ((entry.Removed && observation.Order > entry.RemovedOrder) ||
             (!entry.Removed && (entry.SnapshotObserved || entry.Seen.Count != 0) && observation.Order > entry.EstablishedOrder))))
        { _entries.Remove(identity); entry = new Entry { EstablishedOrder = observation.Order }; _entries.Add(identity, entry); }
        if (entry.Seen.Contains(kind) || (kind == MissionTransitionKind.Restored && entry.Seen.Count != 0)) return;
        var accepted = entry.Accepted || kind == MissionTransitionKind.Accepted;
        var snapshot = new MissionSnapshot(_session.Value, entry.Id, definitionId, name, tags, accepted, entry.Evidence);
        _revision++;
        entry.Seen.Add(kind); entry.Accepted = accepted;
        if (kind is MissionTransitionKind.Accepted or MissionTransitionKind.Restored) entry.EstablishedOrder = observation.Order;
        if (kind is MissionTransitionKind.Completed or MissionTransitionKind.Abandoned or MissionTransitionKind.Removed)
        { entry.Removed = true; entry.RemovedOrder = Math.Max(entry.RemovedOrder, observation.Order); }
        if (kind == MissionTransitionKind.Accepted)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var item = _pending[i]; var prior = item.snapshot;
                if (prior.InstanceId == entry.Id && item.order >= observation.Order && !prior.AcceptanceObserved)
                    _pending[i] = (item.order, item.epoch, item.kind, new MissionSnapshot(prior.SessionId, prior.InstanceId, prior.DefinitionId, prior.Name, prior.ObjectiveTags, true, prior.IdentityEvidence), item.identity);
            }
        }
        _pending.Add((observation.Order, _epoch, kind, snapshot, identity));
    }
    private void End(Observation observation)
    {
        CheckThread(); if (observation.Closed) return;
        if (_disposed || observation.Epoch != _epoch) { observation.Closed = true; return; }
        if (!_stack.Contains(observation)) { observation.Closed = true; return; }
        while (_stack.Count > 0)
        {
            var closed = _stack.Pop(); closed.Closed = true;
            if (closed == observation) break;
        }
        if (_stack.Count == 0) Flush();
    }
    private void Flush()
    {
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_pending.Count > 0 && !_disposed)
            {
                var pending = _pending.OrderBy(item => item.order).ToArray(); _pending.Clear();
                foreach (var item in pending)
                {
                    if (_disposed || item.epoch != _epoch) continue;
                    _dispatchSnapshot = item.snapshot; _dispatchIdentity = item.identity;
                    var transition = new MissionTransition(item.kind, item.snapshot, ++_sequence);
                    foreach (var subscriber in _subscriptions.ToArray())
                    {
                        if (_disposed || item.epoch != _epoch) break;
                        if (!subscriber.Active) continue;
                        try { subscriber.Callback(transition); }
                        catch (Exception error) { try { _report(subscriber.Owner, error); } catch { } }
                    }
                }
            }
        }
        finally { _dispatching = false; _dispatchSnapshot = null; _dispatchIdentity = null; }
    }
    public IDisposable Subscribe(string owner, Action<MissionTransition> callback)
    {
        CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(MissionTransitions));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner required.", nameof(owner));
        var subscription = new Subscription(this, owner, callback ?? throw new ArgumentNullException(nameof(callback)));
        _subscriptions.Add(subscription); return subscription;
    }
    public void Dispose()
    {
        CheckThread(); if (_disposed) return;
        Reset(null); _disposed = true; _subscriptions.Clear();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal interface ICraftingJobSource
{
    CraftingJobListSnapshot ReadJobs(RecipeStationHandle station);
    void ForgetJob(CraftingJobHandle job);
    void InvalidateJobs();
}

internal sealed class CraftingJobService : ICraftingJobs, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly ICraftingJobSource _source;
    private readonly Action<string, Exception> _report;
    private readonly IDisposable _lifetime;
    private readonly Dictionary<CraftingJobHandle, CraftingJobSnapshot> _known = new();
    private readonly HashSet<CraftingJobHandle> _queued = new();
    private readonly List<Subscription> _subscriptions = new();
    private readonly Queue<(long Epoch, CraftingJobEvent Fact)> _pending = new();
    private long _epoch, _sequence, _callbackSequence;
    internal long CallbackContext { get; private set; }
    private bool _available, _disposed, _dispatching;
    internal CraftingJobService(LifecycleHub hub, ICraftingJobSource source, Action<string, Exception> report)
    {
        _hub = hub; _source = source; _report = report;
        _lifetime = hub.Subscribe("vgmodapi.crafting-jobs", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                Invalidate("Session changed.");
        });
    }
    internal Guid? ActiveSession
    {
        get { _hub.CheckThread(); return !_disposed && _available && _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized ? _hub.CurrentSession.Id : null; }
    }
    public bool IsDispatchingCallbacks { get { _hub.CheckThread(); return _dispatching; } }
    internal void SetAvailable(bool available)
    {
        _hub.CheckThread(); if (_disposed) return;
        _available = available;
        _hub.SetCapability("crafting-jobs", available, available ? "Experimental native job observation; not runtime-qualified." : "Crafting job observation unavailable.");
        if (!available) Invalidate("Observation unavailable.");
    }
    public CraftingJobListSnapshot Read(RecipeStationHandle station)
    {
        _hub.CheckThread();
        if (station == null) throw new ArgumentNullException(nameof(station));
        if (_disposed || !_available) return Failure(CraftingJobQueryStatus.IntegrationUnavailable, "Crafting job observation unavailable.");
        var session = ActiveSession;
        if (!session.HasValue) return Failure(CraftingJobQueryStatus.SessionUnavailable, "Gameplay session required.");
        if (session != station.SessionId) return Failure(CraftingJobQueryStatus.StaleHandle, "Station belongs to another session.");
        try
        {
            var result = _source.ReadJobs(station);
            if (ActiveSession != session) return Failure(CraftingJobQueryStatus.SessionUnavailable, "Session changed during query.");
            if (result.Status != CraftingJobQueryStatus.Available) return result;
            if (result.Jobs.Any(job => !job.Handle.Station.Equals(station))) throw new InvalidOperationException("Native job attributed to a different station.");
            var current = new HashSet<CraftingJobHandle>(result.Jobs.Select(job => job.Handle));
            var missing = _known.Values.Where(job => job.Handle.Station.Equals(station) && !current.Contains(job.Handle)).ToArray();
            if (_known.Count + result.Jobs.Count(job => !_known.ContainsKey(job.Handle)) > 16384) throw new RecipeCatalogLimitException();
            foreach (var job in result.Jobs) Remember(job);
            foreach (var job in missing)
                Observe(CraftingJobEventKind.Invalidated, WithState(job, CraftingJobState.Invalidated), Array.Empty<CraftingDeliverySnapshot>(),
                    CraftingDeliveryStatus.NotApplicable, "Job disappeared outside an observed terminal operation; outcome unknown.");
            return ActiveSession == session ? result : Failure(CraftingJobQueryStatus.SessionUnavailable, "Session changed during query callbacks.");
        }
        catch (RecipeCatalogLimitException) { return Failure(CraftingJobQueryStatus.LimitExceeded, "Job query exceeds supported bounds."); }
        catch (Exception error) { Report("vgmodapi.crafting-jobs", error); return Failure(CraftingJobQueryStatus.NativeFailure, "Job query failed; no partial catalog returned."); }
    }
    public IDisposable Subscribe(string pluginId, Action<CraftingJobEvent> callback)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(CraftingJobService));
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 512) throw new ArgumentException("Bounded plugin identity required.", nameof(pluginId));
        var subscription = new Subscription(this, pluginId, callback ?? throw new ArgumentNullException(nameof(callback)));
        _subscriptions.Add(subscription); return subscription;
    }
    internal void Observe(CraftingJobEventKind kind, CraftingJobSnapshot job, IEnumerable<CraftingDeliverySnapshot> deliveries, CraftingDeliveryStatus status, string detail)
    {
        _hub.CheckThread(); if (ActiveSession != job.Handle.Station.SessionId) return;
        if (kind == CraftingJobEventKind.Queued && !_queued.Add(job.Handle)) return;
        if (kind is CraftingJobEventKind.Finished or CraftingJobEventKind.Cancelled or CraftingJobEventKind.Invalidated || job.State != CraftingJobState.Active)
        {
            _known.Remove(job.Handle); _queued.Remove(job.Handle); _source.ForgetJob(job.Handle);
        }
        else Remember(job);
        Enqueue(kind, job, deliveries, status, detail); Dispatch();
    }
    private void Remember(CraftingJobSnapshot job)
    {
        if (_known.Count >= 16384 && !_known.ContainsKey(job.Handle)) throw new RecipeCatalogLimitException();
        _known[job.Handle] = job;
    }
    private void Invalidate(string detail)
    {
        _hub.CheckThread(); _epoch++;
        var retained = _pending.Where(item => item.Fact.Kind == CraftingJobEventKind.Invalidated).ToArray();
        _pending.Clear(); foreach (var item in retained) _pending.Enqueue(item);
        var jobs = _known.Values.ToArray(); _known.Clear(); _queued.Clear(); _source.InvalidateJobs();
        foreach (var job in jobs) Enqueue(CraftingJobEventKind.Invalidated, WithState(job, CraftingJobState.Invalidated),
            Array.Empty<CraftingDeliverySnapshot>(), CraftingDeliveryStatus.NotApplicable, detail);
        Dispatch();
    }
    private void Enqueue(CraftingJobEventKind kind, CraftingJobSnapshot job, IEnumerable<CraftingDeliverySnapshot> deliveries, CraftingDeliveryStatus status, string detail)
    {
        if (_pending.Count >= 16384) throw new RecipeCatalogLimitException();
        _pending.Enqueue((_epoch, new CraftingJobEvent(checked(++_sequence), kind, job, deliveries, status, detail)));
    }
    private void Dispatch()
    {
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            while (_pending.Count != 0 && !_disposed)
            {
                var item = _pending.Dequeue();
                if (item.Epoch != _epoch && item.Fact.Kind != CraftingJobEventKind.Invalidated) continue;
                foreach (var subscription in _subscriptions.ToArray())
                {
                    if (_disposed || item.Epoch != _epoch && item.Fact.Kind != CraftingJobEventKind.Invalidated) break;
                    if (!subscription.Active) continue;
                    var previousContext = CallbackContext;
                    CallbackContext = checked(++_callbackSequence);
                    try { subscription.Callback(item.Fact); } catch (Exception error) { Report(subscription.Owner, error); }
                    finally { CallbackContext = previousContext; }
                }
            }
        }
        finally { _dispatching = false; }
    }
    private void Report(string owner, Exception error) { try { _report(owner, error); } catch { } }
    internal static CraftingJobSnapshot WithState(CraftingJobSnapshot job, CraftingJobState state) => new(job.Handle, job.Recipe, job.Process, state,
        job.InitialBatches, job.RemainingBatches, job.CraftedLevel, job.ProgressRatio, job.SecondsPerBatch);
    internal static CraftingJobListSnapshot Failure(CraftingJobQueryStatus status, string detail) => new(status, detail, Array.Empty<CraftingJobSnapshot>());
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _available = false; _lifetime.Dispose(); _pending.Clear(); _known.Clear(); _queued.Clear();
        foreach (var subscription in _subscriptions) subscription.Active = false;
        _subscriptions.Clear(); _source.InvalidateJobs();
    }
    private sealed class Subscription : IDisposable
    {
        private readonly CraftingJobService _service;
        internal readonly string Owner;
        internal readonly Action<CraftingJobEvent> Callback;
        internal bool Active = true;
        internal Subscription(CraftingJobService service, string owner, Action<CraftingJobEvent> callback) { _service = service; Owner = owner; Callback = callback; }
        public void Dispose() { _service._hub.CheckThread(); Active = false; _service._subscriptions.Remove(this); }
    }
}

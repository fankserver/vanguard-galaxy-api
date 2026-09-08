using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class CraftingJobObserver : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly CraftingJobService _service;
    private readonly RecipeCatalogNativeSource _source;
    private readonly List<Scope> _scopes = new();
    private Exception? _fault;
    private long _epoch;
    private bool _disposed, _faultReported;
    internal CraftingJobObserver(LifecycleHub hub, CraftingJobService service, RecipeCatalogNativeSource source)
    { _hub = hub; _service = service; _source = source; source.JobsInvalidated += Reset; }
    internal Scope? Begin(string key, object instance, object[] args)
    {
        if (_disposed || _fault != null) return null;
        if ((key == "jobInventoryAdd" || key == "jobMaterialAdd") && _scopes.Count == 0) return null;
        try
        {
            _hub.CheckThread(); var session = _service.ActiveSession; if (!session.HasValue) return null;
            if (key == "jobInventoryAdd" || key == "jobMaterialAdd") return BeginTransfer(key, instance, args, session.Value);
            var process = key.EndsWith("Forge", StringComparison.Ordinal) ? RecipeProcess.Forge : RecipeProcess.Refining;
            var isJob = key.StartsWith("jobBatch", StringComparison.Ordinal) || key.StartsWith("jobRoute", StringComparison.Ordinal);
            var parent = isJob ? Value(instance, "parent") : instance;
            if (parent == null) return null;
            var station = Value(parent, "spaceStation"); if (station == null) return null;
            var handle = _source.IssueStation(session.Value, station); if (handle == null) return null;
            if (!ReferenceEquals(Value(station, process == RecipeProcess.Forge ? "forge" : "refinery"), parent)) return null;
            var scope = new Scope(key, instance, args, _epoch, session.Value, _source.NativePlayer)
            { Parent = parent, Station = handle, Process = process };
            var nativeJobs = _source.NativeJobs(parent);
            if (isJob || key.StartsWith("jobCancel", StringComparison.Ordinal))
            {
                var job = isJob ? instance : args[0];
                if (!nativeJobs.Any(candidate => ReferenceEquals(candidate, job))) return null;
                scope.Job = job; scope.Before = _source.SnapshotJob(handle, parent, job, process);
            }
            else foreach (var job in nativeJobs) scope.BeforeJobs.Add(job, _source.SnapshotJob(handle, parent, job, process));
            Push(scope); return scope;
        }
        catch (Exception error) { Interlocked.CompareExchange(ref _fault, error, null); return null; }
    }
    internal void End(Scope? scope, object? result, Exception? originalError)
    {
        if (scope == null || _disposed) return;
        try
        {
            _hub.CheckThread(); if (!Current(scope)) return;
            if (_scopes.Count == 0 || !ReferenceEquals(_scopes[_scopes.Count - 1], scope)) throw new InvalidOperationException("Unbalanced crafting observation scopes.");
            _scopes.RemoveAt(_scopes.Count - 1);
            if (scope.Transfer != null) { EndTransfer(scope, result, originalError); return; }
            if (scope.Key.StartsWith("jobRoute", StringComparison.Ordinal))
            {
                var owner = _scopes.LastOrDefault(item => item.Key.StartsWith("jobBatch", StringComparison.Ordinal) && ReferenceEquals(item.Job, scope.Job));
                if (owner != null) { owner.Deliveries.AddRange(scope.Deliveries); owner.Unresolved |= originalError != null || scope.Unresolved || scope.Deliveries.Count == 0; }
                return;
            }
            if (scope.Key.StartsWith("jobQueue", StringComparison.Ordinal))
            {
                var added = _source.NativeJobs(scope.Parent!).Where(job => !scope.BeforeJobs.ContainsKey(job) && !scope.NestedTerminals.Contains(job) && !scope.NestedQueued.Contains(job))
                    .Select(job => (Native: job, Snapshot: _source.SnapshotJob(scope.Station!, scope.Parent!, job, scope.Process))).ToArray();
                foreach (var item in added)
                {
                    if (!Current(scope)) break;
                    foreach (var ancestor in _scopes) ancestor.NestedQueued.Add(item.Native);
                    _service.Observe(CraftingJobEventKind.Queued, item.Snapshot, Array.Empty<CraftingDeliverySnapshot>(), CraftingDeliveryStatus.NotApplicable,
                        originalError == null ? "New native queue entry observed; not a payment receipt." : "Queue entry exists despite an operation exception.");
                }
                return;
            }
            if (scope.Key.StartsWith("jobProgress", StringComparison.Ordinal))
            {
                var present = new HashSet<object>(_source.NativeJobs(scope.Parent!), NativeObjectIdentity.Instance);
                var removed = scope.BeforeJobs.Where(pair => !present.Contains(pair.Key) && !scope.NestedTerminals.Contains(pair.Key))
                    .Select(pair => (Native: pair.Key, Snapshot: _source.SnapshotJob(scope.Station!, scope.Parent!, pair.Key, scope.Process, pair.Value.Handle))).ToArray();
                foreach (var item in removed)
                {
                    if (!Current(scope)) break;
                    var finished = originalError == null && item.Snapshot.RemainingBatches == 0;
                    Terminal(scope, item.Native, item.Snapshot, finished ? CraftingJobEventKind.Finished : CraftingJobEventKind.Invalidated,
                        finished ? CraftingJobState.Finished : CraftingJobState.Invalidated, Array.Empty<CraftingDeliverySnapshot>(), CraftingDeliveryStatus.NotApplicable,
                        finished ? "Native processing ended; output arrival is established only by batch transfer observations." : "Queue entry removed with an unresolved outcome.");
                }
                return;
            }
            var after = _source.SnapshotJob(scope.Station!, scope.Parent!, scope.Job!, scope.Process, scope.Before!.Handle);
            if (!_source.NativeJobs(scope.Parent!).Any(job => ReferenceEquals(job, scope.Job)))
                after = CraftingJobService.WithState(after, CraftingJobState.Invalidated);
            if (scope.Key.StartsWith("jobBatch", StringComparison.Ordinal))
            {
                var verified = after.State == CraftingJobState.Active && originalError == null && !scope.Unresolved && scope.Before!.RemainingBatches - after.RemainingBatches == 1 &&
                    scope.Deliveries.Count > 0 && scope.Deliveries.All(item => item.Status == CraftingDeliveryStatus.Verified && item.VerifiedAmount == item.RequestedAmount);
                _service.Observe(CraftingJobEventKind.BatchObserved, after, scope.Deliveries,
                    verified ? CraftingDeliveryStatus.Verified : CraftingDeliveryStatus.Unresolved,
                    originalError == null ? "One native batch call observed; remaining count alone is not delivery evidence." : "Batch call faulted; recorded transfers may be partial.");
            }
            else if (scope.Key.StartsWith("jobCancel", StringComparison.Ordinal))
            {
                var removed = !_source.NativeJobs(scope.Parent!).Any(job => ReferenceEquals(job, scope.Job));
                if (removed && originalError == null)
                    Terminal(scope, scope.Job!, after, CraftingJobEventKind.Cancelled, CraftingJobState.Cancelled, scope.Deliveries,
                        CraftingDeliveryStatus.Unresolved, "Native cancellation removed the job. Resource transfers are separate from the unobserved credit refund.");
                else _service.Observe(CraftingJobEventKind.OperationFaulted, after, scope.Deliveries, CraftingDeliveryStatus.Unresolved,
                    "Cancellation did not establish a clean terminal outcome; refunds may be partial.");
            }
        }
        catch (Exception error) { Interlocked.CompareExchange(ref _fault, error, null); }
    }
    private void Terminal(Scope scope, object native, CraftingJobSnapshot snapshot, CraftingJobEventKind kind, CraftingJobState state,
        IEnumerable<CraftingDeliverySnapshot> deliveries, CraftingDeliveryStatus status, string detail)
    {
        foreach (var ancestor in _scopes) ancestor.NestedTerminals.Add(native);
        if (Current(scope)) _service.Observe(kind, CraftingJobService.WithState(snapshot, state), deliveries, status, detail);
    }
    private void Push(Scope scope)
    {
        if (_scopes.Count >= 64) throw new RecipeCatalogLimitException();
        _scopes.Add(scope);
    }
    private bool Current(Scope scope) => !_disposed && _fault == null && scope.Epoch == _epoch &&
        _service.ActiveSession == scope.Session && ReferenceEquals(scope.Player, _source.NativePlayer);
    private void Reset() { _epoch++; _scopes.Clear(); }
    internal Exception? PumpFault()
    {
        _hub.CheckThread(); if (_fault == null || _disposed || _faultReported) return null;
        _faultReported = true; _service.SetAvailable(false); Reset(); return _fault;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _source.JobsInvalidated -= Reset; Reset();
    }
    private static object? Value(object value, string member) => RecipeCatalogNativeSource.Member(value, member);
    internal sealed class Scope
    {
        internal readonly string Key;
        internal readonly object Instance;
        internal readonly object[] Args;
        internal readonly long Epoch;
        internal readonly Guid Session;
        internal readonly object? Player;
        internal object? Parent, Job;
        internal RecipeStationHandle? Station;
        internal RecipeProcess Process;
        internal CraftingJobSnapshot? Before;
        internal readonly Dictionary<object, CraftingJobSnapshot> BeforeJobs = new(NativeObjectIdentity.Instance);
        internal readonly HashSet<object> NestedTerminals = new(NativeObjectIdentity.Instance), NestedQueued = new(NativeObjectIdentity.Instance);
        internal readonly List<CraftingDeliverySnapshot> Deliveries = new();
        internal bool Unresolved;
        internal TransferState? Transfer;
        internal Scope(string key, object instance, object[] args, long epoch, Guid session, object? player)
        { Key = key; Instance = instance; Args = args.ToArray(); Epoch = epoch; Session = session; Player = player; }
    }
}

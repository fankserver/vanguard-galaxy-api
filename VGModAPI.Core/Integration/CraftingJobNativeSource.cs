using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource : ICraftingJobSource
{
    private readonly Dictionary<object, CraftingJobHandle> _jobHandles = new(NativeObjectIdentity.Instance);
    private readonly Dictionary<CraftingJobHandle, object> _jobObjects = new();
    internal event Action? JobsInvalidated;
    public void InvalidateJobs() { _jobHandles.Clear(); _jobObjects.Clear(); JobsInvalidated?.Invoke(); }
    public void ForgetJob(CraftingJobHandle handle)
    {
        if (_jobObjects.TryGetValue(handle, out var job)) { _jobObjects.Remove(handle); _jobHandles.Remove(job); }
    }
    public CraftingJobListSnapshot ReadJobs(RecipeStationHandle handle)
    {
        var station = ResolveStation(handle);
        if (station == null) return CraftingJobService.Failure(CraftingJobQueryStatus.StaleHandle, "Station was not issued here, or was removed/replaced.");
        var jobs = new List<CraftingJobSnapshot>();
        foreach (var pair in new[] { (Name: "forge", Process: RecipeProcess.Forge), (Name: "refinery", Process: RecipeProcess.Refining) })
        {
            var parent = Get(station, pair.Name); if (parent == null) continue;
            foreach (var job in NativeJobs(parent))
            {
                if (jobs.Count >= 4096) throw new RecipeCatalogLimitException();
                jobs.Add(SnapshotJob(handle, parent, job, pair.Process));
            }
        }
        return new CraftingJobListSnapshot(CraftingJobQueryStatus.Available, "Native queue snapshot; reconstructed jobs are not new queue events.", jobs);
    }
    internal object? ResolveStation(RecipeStationHandle handle) => handle.SessionId == _quoteSession &&
        _quoteStations.TryGetValue(handle.InstanceId, out var station) && StationStillPresent(station) ? station : null;
    internal object[] NativeJobs(object parent)
    {
        var jobs = new List<object>();
        var seen = new HashSet<object>(NativeObjectIdentity.Instance);
        foreach (var job in Enumerate(Get(parent, "jobs")))
        {
            if (jobs.Count >= 4096) throw new RecipeCatalogLimitException();
            if (!seen.Add(job)) throw new InvalidOperationException("Duplicate native job reference.");
            jobs.Add(job);
        }
        return jobs.ToArray();
    }
    internal CraftingJobSnapshot SnapshotJob(RecipeStationHandle station, object parent, object job, RecipeProcess process, CraftingJobHandle? observedHandle = null)
    {
        var nativeStation = ResolveStation(station);
        if (nativeStation == null || !ReferenceEquals(Get(nativeStation, process == RecipeProcess.Forge ? "forge" : "refinery"), parent) ||
            !ReferenceEquals(Get(job, "parent"), parent)) throw new InvalidOperationException("Job owning station or process instance is stale.");
        var definition = Get(job, process == RecipeProcess.Forge ? "recipe" : "ore") ?? throw new InvalidOperationException("Job definition is unresolved.");
        var nativeId = process == RecipeProcess.Forge ? Text(definition, "identifier") :
            Text(Get(definition, "item") ?? throw new InvalidOperationException("Ore identity unavailable."), "identifier");
        var isNew = !_jobHandles.TryGetValue(job, out var handle);
        if (observedHandle != null)
        {
            if (handle != null && !handle.Equals(observedHandle)) throw new InvalidOperationException("Job identity changed during observation.");
            handle = observedHandle; isNew = false;
        }
        if (isNew)
        {
            if (_jobHandles.Count >= 16384) throw new RecipeCatalogLimitException();
            handle = new CraftingJobHandle(station, Guid.NewGuid());
        }
        if (!handle!.Station.Equals(station)) throw new InvalidOperationException("Job moved between owning stations without reconstruction.");
        var duration = Convert.ToDouble(Get(job, process == RecipeProcess.Forge ? "craftingTime" : "refineTime"));
        var progress = Convert.ToDouble(Get(job, "jobProgress"));
        var snapshot = new CraftingJobSnapshot(handle, new RecipeId("vanilla", (process == RecipeProcess.Forge ? "forge/" : "refining/") + nativeId),
            process, CraftingJobState.Active, Convert.ToInt32(Get(job, "initialAmount")), Convert.ToInt32(Get(job, "remainingAmount")),
            process == RecipeProcess.Forge ? Convert.ToInt32(Get(job, "craftedLevel")) : null,
            Finite(progress) && progress >= 0 ? Math.Min(1, progress) : null, Finite(duration) && duration > 0 ? duration : null);
        if (isNew) { _jobHandles.Add(job, handle); _jobObjects.Add(handle, job); }
        return snapshot;
    }
    internal object? NativePlayer => GetStatic("Source.Player.GamePlayer", "current");
    internal static object? Member(object value, string name) => Get(value, name);
    internal static object? InvokeMember(object value, string name, params object[] args) => Call(value, name, args);
    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

internal sealed class NativeObjectIdentity : IEqualityComparer<object>
{
    internal static readonly NativeObjectIdentity Instance = new();
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
}

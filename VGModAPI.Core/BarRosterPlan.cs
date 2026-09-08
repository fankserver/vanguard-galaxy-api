using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class BarRosterPlan
{
    internal Guid Session { get; }
    internal string Station { get; }
    internal object Revision { get; }
    internal BarRosterPolicy.Decision Policy { get; }
    internal IReadOnlyList<BarPatronState> Patrons { get; }
    internal Func<StoryContentId, Guid, bool>? MissionReady { get; }
    internal Func<object>? DependencyStamp { get; }
    internal BarRosterPlan(Guid session, string station, object revision, BarRosterPolicy.Decision policy, IEnumerable<BarPatronState> patrons,
        Func<StoryContentId, Guid, bool>? missionReady, Func<object>? dependencyStamp)
    {
        Session = session; Station = station; Revision = revision; Policy = policy;
        MissionReady = missionReady; DependencyStamp = dependencyStamp;
        Patrons = Array.AsReadOnly(patrons.OrderBy(row => row.Id.Provider, StringComparer.Ordinal)
            .ThenBy(row => row.Id.LocalId, StringComparer.Ordinal).ToArray());
    }
}

internal sealed partial class BarContentService
{
    private object _revision = new();
    private void Changed() => _revision = new object();

    internal bool IsCurrent(BarRosterPlan plan)
    {
        _checkThread();
        if (_disposed || !ReferenceEquals(plan.Revision, _revision) || !_persistence.CanMutate(plan.Session)) return false;
        var current = Plan(plan.Session, plan.Station, plan.MissionReady, plan.DependencyStamp);
        return current != null && ReferenceEquals(plan.Revision, current.Revision)
            && plan.Policy.KeepVanilla == current.Policy.KeepVanilla
            && plan.Policy.Admitted.SequenceEqual(current.Policy.Admitted)
            && plan.Policy.Denied.Count == current.Policy.Denied.Count
            && plan.Policy.Denied.All(pair => current.Policy.Denied.TryGetValue(pair.Key, out var reason) && reason == pair.Value)
            && _persistence.CanMutate(plan.Session) && ReferenceEquals(plan.Revision, _revision);
    }

    // A dependency stamp is an opaque immutable token. The integration must replace it whenever
    // any referenced mission's availability changes, including changes made during resolution.
    // Missing stamps fail closed; repeated boolean queries cannot prove a coherent multi-mission view.
    internal BarRosterPlan? Plan(Guid session, string station, Func<StoryContentId, Guid, bool>? missionReady = null, Func<object>? dependencyStamp = null)
    {
        _checkThread();
        if (_disposed || !_persistence.Read(session, out var saved)) return null;
        var revision = _revision;
        var candidates = saved.Where(row => row.Station == station && _leases.TryGetValue(row.Id.Provider, out var lease)
            && lease.Definitions.TryGetValue(row.Id.LocalId, out var definition) && definition.Retention == BarPatronRetention.Persistent)
            .Concat(_transient.Values.Where(row => row.Station == station)).ToArray();
        // The host must replace this token on every permission change, including changes made
        // by mission resolution. Stamp accessors are read-only and must not return recycled tokens.
        object? permissionStamp = null;
        if (_leases.Values.Any(lease => lease.Stations.TryGetValue(station, out var mode) && mode == BarRosterOwnership.Exclusive))
        {
            try { permissionStamp = _permissionStamp?.Invoke(); } catch { return null; }
            if (permissionStamp == null) return null;
        }
        var claims = new List<BarRosterPolicy.Claim>();
        foreach (var lease in _leases.Values.ToArray())
        {
            if (!lease.Stations.ContainsKey(station) && !candidates.Any(row => row.Id.Provider == lease.ProviderId)) continue;
            bool exclusive = lease.Stations.TryGetValue(station, out var ownership) && ownership == BarRosterOwnership.Exclusive;
            bool allowed = !exclusive;
            if (exclusive) { try { allowed = _exclusivePermission(lease.PluginId); } catch { allowed = false; } }
            if (_disposed || !ReferenceEquals(revision, _revision) || !_persistence.Read(session, out _)) return null;
            claims.Add(new BarRosterPolicy.Claim(lease.ProviderId, exclusive, allowed));
        }
        var policy = BarRosterPolicy.Resolve(claims);
        var admitted = candidates.Where(row => policy.Admitted.Contains(row.Id.Provider)).ToArray();
        object? stamp = null;
        if (admitted.Any(row => row.Mission.HasValue))
        {
            try { stamp = dependencyStamp?.Invoke(); } catch { return null; }
            if (stamp == null) return null;
        }
        foreach (var row in admitted)
        {
            if (!row.Mission.HasValue) continue;
            bool ready;
            try { ready = missionReady != null && missionReady(row.Mission.Value, row.Occurrence!.Value); }
            catch { ready = false; }
            if (!ready || _disposed || !ReferenceEquals(revision, _revision) || !_persistence.Read(session, out _)) return null;
        }
        if (stamp != null)
        {
            try { if (!ReferenceEquals(stamp, dependencyStamp!())) return null; } catch { return null; }
        }
        if (permissionStamp != null)
        {
            try { if (!ReferenceEquals(permissionStamp, _permissionStamp!())) return null; } catch { return null; }
        }
        if (_disposed || !ReferenceEquals(revision, _revision) || !_persistence.Read(session, out _)) return null;
        return new BarRosterPlan(session, station, revision, policy, admitted, missionReady, dependencyStamp);
    }
}

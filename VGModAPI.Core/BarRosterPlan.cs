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
    internal BarRosterPlan(Guid session, string station, object revision, BarRosterPolicy.Decision policy, IEnumerable<BarPatronState> patrons)
    {
        Session = session; Station = station; Revision = revision; Policy = policy;
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
        return !_disposed && ReferenceEquals(plan.Revision, _revision) && _persistence.Read(plan.Session, out _);
    }

    internal BarRosterPlan? Plan(Guid session, string station, Func<StoryContentId, Guid, bool>? missionReady = null)
    {
        _checkThread();
        if (_disposed || !_persistence.Read(session, out var saved)) return null;
        var revision = _revision;
        var candidates = saved.Where(row => row.Station == station && _leases.TryGetValue(row.Id.Provider, out var lease)
            && lease.Definitions.TryGetValue(row.Id.LocalId, out var definition) && definition.Retention == BarPatronRetention.Persistent)
            .Concat(_transient.Values.Where(row => row.Station == station)).ToArray();
        var claims = _leases.Values.Where(lease => lease.Stations.ContainsKey(station) || candidates.Any(row => row.Id.Provider == lease.ProviderId))
            .Select(lease => new BarRosterPolicy.Claim(lease.ProviderId,
                lease.Stations.TryGetValue(station, out var ownership) && ownership == BarRosterOwnership.Exclusive, true)).ToArray();
        var policy = BarRosterPolicy.Resolve(claims);
        var admitted = candidates.Where(row => policy.Admitted.Contains(row.Id.Provider)).ToArray();
        foreach (var row in admitted)
        {
            if (!row.Mission.HasValue) continue;
            bool ready;
            try { ready = missionReady != null && missionReady(row.Mission.Value, row.Occurrence!.Value); }
            catch { ready = false; }
            if (!ready || _disposed || !ReferenceEquals(revision, _revision) || !_persistence.Read(session, out _)) return null;
        }
        return new BarRosterPlan(session, station, revision, policy, admitted);
    }
}

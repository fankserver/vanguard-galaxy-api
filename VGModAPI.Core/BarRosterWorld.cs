using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class BarRosterSnapshot
{
    internal object Token { get; }
    internal IReadOnlyList<object> VanillaPatrons { get; }
    internal int Capacity { get; }
    internal BarRosterSnapshot(object token, IEnumerable<object> vanillaPatrons, int capacity)
    {
        Token = token ?? throw new ArgumentNullException(nameof(token));
        if (capacity < 0 || capacity > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        var patrons = vanillaPatrons.Take(33).ToArray();
        if (patrons.Length > capacity || patrons.Any(patron => patron == null)) throw new ArgumentException("Invalid native roster snapshot.");
        VanillaPatrons = Array.AsReadOnly(patrons); Capacity = capacity;
    }
}

internal interface IBarRosterWorld
{
    BarRosterSnapshot? Capture(string station);
    // Construct inert contacts only: no installation, interaction or persistent state writes.
    object? CreateContact(BarPatronState state);
    // Recheck exact station/roster references and stillValid immediately before the atomic swap.
    bool Apply(BarRosterSnapshot snapshot, IReadOnlyList<object> patrons, Func<bool> stillValid);
}

internal enum BarRosterApplyStatus { Applied, Unavailable, CapacityExceeded }

internal static class BarRosterApplication
{
    internal static BarRosterApplyStatus Apply(BarContentService service, IBarRosterWorld world, BarRosterPlan plan)
    {
        if (!service.IsCurrent(plan)) return BarRosterApplyStatus.Unavailable;
        var snapshot = world.Capture(plan.Station);
        if (snapshot == null || !service.IsCurrent(plan)) return BarRosterApplyStatus.Unavailable;
        var result = plan.Policy.KeepVanilla ? snapshot.VanillaPatrons.ToList() : new List<object>();
        if (result.Count + plan.Patrons.Count > snapshot.Capacity) return BarRosterApplyStatus.CapacityExceeded;
        foreach (var patron in plan.Patrons)
        {
            var contact = world.CreateContact(patron);
            if (contact == null || !service.IsCurrent(plan)) return BarRosterApplyStatus.Unavailable;
            result.Add(contact);
        }
        return world.Apply(snapshot, result.AsReadOnly(), () => service.IsCurrent(plan))
            ? BarRosterApplyStatus.Applied : BarRosterApplyStatus.Unavailable;
    }
}

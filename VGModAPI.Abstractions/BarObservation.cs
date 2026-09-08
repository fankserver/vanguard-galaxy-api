using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

/// <summary>Immutable presentation identity for cache observation, not a native object handle.</summary>
public sealed class BarRosterMember
{
    public BarPatronId? OwnedId { get; }
    public string NativeKind { get; }
    public string Seed { get; }
    public int Seat { get; }
    public BarRosterMember(BarPatronId? ownedId, string nativeKind, string seed, int seat)
    {
        if (string.IsNullOrWhiteSpace(nativeKind)) throw new ArgumentException("Native kind required.", nameof(nativeKind));
        if (seed == null) throw new ArgumentNullException(nameof(seed));
        if (seat < 1 || seat > 32) throw new ArgumentOutOfRangeException(nameof(seat));
        OwnedId = ownedId; NativeKind = nativeKind; Seed = seed; Seat = seat;
    }
}

/// <summary>The roster after policy application. Observation must not mutate the roster or start dialogue.</summary>
public sealed class BarRosterFinalized
{
    public Guid SessionId { get; }
    public string StationId { get; }
    public IReadOnlyList<BarRosterMember> Members { get; }
    public IReadOnlyDictionary<string, string> DeniedProviders { get; }
    public BarRosterFinalized(Guid sessionId, string stationId, IEnumerable<BarRosterMember> members,
        IReadOnlyDictionary<string, string> deniedProviders)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(stationId)) throw new ArgumentException("Station required.", nameof(stationId));
        var copy = (members ?? throw new ArgumentNullException(nameof(members))).Take(33).ToArray();
        if (copy.Length > 32 || copy.Any(member => member == null)) throw new ArgumentException("Invalid roster.", nameof(members));
        var denied = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in deniedProviders ?? throw new ArgumentNullException(nameof(deniedProviders))) denied.Add(pair.Key, pair.Value);
        SessionId = sessionId; StationId = stationId; Members = Array.AsReadOnly(copy);
        DeniedProviders = new ReadOnlyDictionary<string, string>(denied);
    }
}

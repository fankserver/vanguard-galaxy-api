using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Crew already removed from an approaching donor, but not yet placed in native pods.</summary>
internal sealed class DungeonDonorApproachState
{
    internal string ShipId { get; }
    internal IReadOnlyDictionary<string, int> Crew { get; }
    internal DungeonDonorApproachState(string shipId, IReadOnlyDictionary<string, int> crew)
    {
        if (string.IsNullOrWhiteSpace(shipId) || shipId.Length > 128) throw new ArgumentException("Invalid donor identity.");
        if (crew == null || crew.Count == 0 || crew.Count > 64 || crew.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value <= 0 || pair.Value > 10000) || crew.Values.Sum(value => (long)value) > 10000)
            throw new ArgumentException("Invalid reserved donor crew.");
        ShipId = shipId; Crew = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(crew, StringComparer.Ordinal));
    }
}

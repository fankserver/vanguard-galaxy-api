using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Observed roster additions and successfully created overflow, not merely a native method return.</summary>
internal sealed class DungeonPodDeliveryReceipt
{
    internal IReadOnlyDictionary<string, int> Accepted { get; }
    internal IReadOnlyDictionary<string, int> OverflowCreated { get; }
    internal DungeonPodDeliveryReceipt(IReadOnlyDictionary<string, int> accepted, IReadOnlyDictionary<string, int> overflowCreated)
    {
        Accepted = Copy(accepted); OverflowCreated = Copy(overflowCreated);
    }
    private static IReadOnlyDictionary<string, int> Copy(IReadOnlyDictionary<string, int> counts)
    {
        if (counts == null || counts.Count > 64 || counts.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0 || pair.Value > 10000))
            throw new ArgumentException("Invalid observed crew delivery counts.");
        return new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(counts, StringComparer.Ordinal));
    }
    internal bool AccountsFor(IReadOnlyDictionary<string, int> manifest)
    {
        if (Accepted.Keys.Concat(OverflowCreated.Keys).Any(key => !manifest.ContainsKey(key))) return false;
        foreach (var pair in manifest)
        {
            Accepted.TryGetValue(pair.Key, out var accepted); OverflowCreated.TryGetValue(pair.Key, out var overflow);
            if ((long)accepted + overflow != pair.Value) return false;
        }
        return true;
    }
}

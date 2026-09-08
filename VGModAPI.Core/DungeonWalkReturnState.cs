using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

internal enum DungeonWalkReturnProgress { Pending, Attempted, Delivered }

/// <summary>Walk crew settlement is independent of terminal rewards and cosmetic inbound walkers.</summary>
internal sealed class DungeonWalkReturnState
{
    internal DungeonWalkReturnProgress Progress { get; }
    internal IReadOnlyDictionary<string, int> Crew { get; }
    internal DungeonWalkReturnState(IReadOnlyDictionary<string, int> crew, DungeonWalkReturnProgress progress = DungeonWalkReturnProgress.Pending)
    {
        if (!Enum.IsDefined(typeof(DungeonWalkReturnProgress), progress) || crew == null || crew.Count > 64 ||
            crew.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value <= 0 || pair.Value > 10000) || crew.Values.Sum(value => (long)value) > 10000)
            throw new ArgumentException("Invalid walk return obligation.");
        Crew = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(crew, StringComparer.Ordinal)); Progress = progress;
    }
    internal DungeonWalkReturnState Begin()
    {
        if (Progress != DungeonWalkReturnProgress.Pending) throw new InvalidOperationException("Walk return cannot be retried.");
        return new(Crew, DungeonWalkReturnProgress.Attempted);
    }
    internal DungeonWalkReturnState Complete(DungeonPodDeliveryReceipt receipt)
    {
        if (Progress != DungeonWalkReturnProgress.Attempted || receipt == null || !receipt.AccountsFor(Crew)) throw new InvalidOperationException("Walk return receipt is incomplete.");
        return new(Crew, DungeonWalkReturnProgress.Delivered);
    }
}

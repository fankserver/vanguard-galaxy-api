using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

internal enum DungeonPodPhase { Docked, Launching, Attached, Returning, Arrived }

/// <summary>Supplemental saved return obligation; outbound crew is not a substitute for the actual return manifest.</summary>
internal sealed class DungeonPodResumeState
{
    internal Guid Id { get; }
    internal Guid Occurrence { get; }
    internal string ParentShipId { get; }
    internal DungeonPodTransport? Transport { get; }
    internal DungeonPodPhase Phase { get; }
    internal bool PlayerOwned { get; }
    internal bool ReturnManifestKnown { get; }
    internal bool ReturnDelivered { get; }
    internal bool ReturnAttempted { get; }
    internal IReadOnlyDictionary<string, int> ReturnCrew { get; }
    internal DungeonPodResumeState(Guid id, Guid occurrence, DungeonPodPhase phase, bool playerOwned,
        bool returnManifestKnown, bool returnDelivered, IEnumerable<KeyValuePair<string, int>> returnCrew, bool returnAttempted = false, string parentShipId = "", DungeonPodTransport? transport = null)
    {
        if (id == Guid.Empty || occurrence == Guid.Empty || !Enum.IsDefined(typeof(DungeonPodPhase), phase)) throw new ArgumentException("Invalid saved pod identity or phase.");
        var copy = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in returnCrew ?? throw new ArgumentNullException(nameof(returnCrew)))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value < 1 || pair.Value > 10000) throw new ArgumentException("Invalid saved return crew.");
            copy.Add(pair.Key, pair.Value);
        }
        if (copy.Count > 64 || copy.Values.Sum(v => (long)v) > 10000) throw new ArgumentException("Saved pod crew exceeds bounds.");
        if (!returnManifestKnown && copy.Count != 0) throw new ArgumentException("An unknown manifest cannot contain inferred crew.");
        if (returnDelivered && (!returnManifestKnown || phase != DungeonPodPhase.Arrived)) throw new ArgumentException("Delivery requires an observed arrival and known manifest.");
        if (parentShipId == null || parentShipId.Length > 128 || parentShipId.IndexOf('\0') >= 0) throw new ArgumentException("Invalid parent ship identity.");
        ParentShipId = parentShipId; Transport = transport;
        Id = id; Occurrence = occurrence; Phase = phase; PlayerOwned = playerOwned; ReturnManifestKnown = returnManifestKnown;
        ReturnAttempted = returnAttempted || returnDelivered;
        ReturnDelivered = returnDelivered; ReturnCrew = new ReadOnlyDictionary<string, int>(copy);
    }
    internal bool RequiresRecovery => Phase is DungeonPodPhase.Returning or DungeonPodPhase.Arrived && !ReturnDelivered;
    internal bool CanRecover => RequiresRecovery && ReturnManifestKnown && !ReturnAttempted;
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Pod reconstruction data retained independently of a target that may depart after capture.</summary>
internal sealed class DungeonPodTransport
{
    internal string NativePodId { get; }
    internal bool PendingReinforcement { get; }
    internal IReadOnlyDictionary<string, int> OutboundCrew { get; }
    // Native world position, angle, hull offset, last target position and attachment offset.
    internal IReadOnlyList<float> Pose { get; }
    internal DungeonPodTransport(string nativePodId, bool pendingReinforcement, IReadOnlyDictionary<string, int> outboundCrew, IEnumerable<float> pose)
    {
        if (string.IsNullOrWhiteSpace(nativePodId) || nativePodId.Length > 128 || nativePodId.IndexOf('\0') >= 0) throw new ArgumentException("Invalid native pod identifier.");
        var values = pose?.ToArray() ?? throw new ArgumentNullException(nameof(pose));
        if (values.Length != 9 || values.Any(value => float.IsNaN(value) || float.IsInfinity(value))) throw new ArgumentException("Invalid pod reconstruction pose.");
        if (outboundCrew == null || outboundCrew.Count > 64 || outboundCrew.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value <= 0 || pair.Value > 10000) || outboundCrew.Values.Sum(value => (long)value) > 10000)
            throw new ArgumentException("Invalid outbound pod crew.");
        NativePodId = nativePodId; PendingReinforcement = pendingReinforcement;
        Pose = Array.AsReadOnly(values); OutboundCrew = new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(outboundCrew, StringComparer.Ordinal));
    }
}

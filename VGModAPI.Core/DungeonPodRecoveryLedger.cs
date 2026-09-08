using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Return effects are attempted at most once per restored save state; failed attempts require explicit diagnosis.</summary>
internal sealed class DungeonPodRecoveryLedger
{
    private Dictionary<Guid, DungeonPodResumeState> _pods = new();
    internal IReadOnlyList<DungeonPodResumeState> Snapshot => Array.AsReadOnly(_pods.Values.ToArray());
    internal DungeonPodResumeState? Get(Guid id) => _pods.TryGetValue(id, out var pod) ? pod : null;
    internal void Restore(byte[]? payload)
    {
        var decoded = payload == null ? Array.Empty<DungeonPodResumeState>() : DungeonPodResumeCodec.Decode(payload);
        _pods = decoded.ToDictionary(p => p.Id);
    }
    internal byte[] Capture() => DungeonPodResumeCodec.Encode(_pods.Values);
    internal void Track(DungeonPodResumeState pod)
    {
        if (_pods.TryGetValue(pod.Id, out var previous))
        {
            if (previous.Occurrence != pod.Occurrence || previous.ParentShipId != pod.ParentShipId || previous.PlayerOwned != pod.PlayerOwned) throw new InvalidOperationException("Pod identity cannot change occurrence.");
            if (previous.ReturnAttempted && (previous.ReturnManifestKnown != pod.ReturnManifestKnown ||
                previous.ReturnCrew.Count != pod.ReturnCrew.Count || previous.ReturnCrew.Any(pair => !pod.ReturnCrew.TryGetValue(pair.Key, out var count) || count != pair.Value)))
                throw new InvalidOperationException("An attempted return manifest cannot change.");
            if (previous.ReturnAttempted && !pod.ReturnAttempted) throw new InvalidOperationException("A recorded return attempt cannot be undone.");
            if (previous.ReturnDelivered && !pod.ReturnDelivered) throw new InvalidOperationException("A delivered return cannot become pending.");
        }
        DungeonPodResumeCodec.Encode(_pods.Values.Where(p => p.Id != pod.Id).Concat(new[] { pod }));
        _pods[pod.Id] = pod;
    }
    internal DungeonPodResumeState? BeginReturn(Guid id)
    {
        var pod = Get(id); if (pod == null || !pod.CanRecover) return null;
        Track(new(pod.Id, pod.Occurrence, pod.Phase, pod.PlayerOwned, true, false, pod.ReturnCrew, true, pod.ParentShipId, pod.Transport));
        return pod;
    }
    internal void Delivered(Guid id)
    {
        var pod = Get(id) ?? throw new InvalidOperationException("Unknown pod.");
        if (!pod.ReturnAttempted || !pod.ReturnManifestKnown) throw new InvalidOperationException("Return was not attempted with a known manifest.");
        Track(new(pod.Id, pod.Occurrence, DungeonPodPhase.Arrived, pod.PlayerOwned, true, true, pod.ReturnCrew, true, pod.ParentShipId, pod.Transport));
    }
}

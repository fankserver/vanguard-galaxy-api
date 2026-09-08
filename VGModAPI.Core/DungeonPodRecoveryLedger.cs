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
            if (previous.OperationId != pod.OperationId || previous.ParentShipId != pod.ParentShipId || previous.PlayerOwned != pod.PlayerOwned) throw new InvalidOperationException("Pod operation, recipient and ownership cannot change.");
            if (previous.ReturnAttempted && (previous.ReturnManifestKnown != pod.ReturnManifestKnown ||
                previous.ReturnCrew.Count != pod.ReturnCrew.Count || previous.ReturnCrew.Any(pair => !pod.ReturnCrew.TryGetValue(pair.Key, out var count) || count != pair.Value)))
                throw new InvalidOperationException("An attempted return manifest cannot change.");
            if (previous.ReturnAttempted && !pod.ReturnAttempted) throw new InvalidOperationException("A recorded return attempt cannot be undone.");
            if (previous.ReturnDelivered && !pod.ReturnDelivered) throw new InvalidOperationException("A delivered return cannot become pending.");
        }
        DungeonPodResumeCodec.Encode(_pods.Values.Where(p => p.Id != pod.Id).Concat(new[] { pod }));
        _pods[pod.Id] = pod;
    }
    internal IReadOnlyList<DungeonPodResumeState>? BeginDockedRefunds(IReadOnlyList<Guid> ids, Guid operationId, string recipient)
    {
        if (ids.Count > 256 || ids.Distinct().Count() != ids.Count) return null;
        var next = new Dictionary<Guid, DungeonPodResumeState>(_pods); var attempts = new List<DungeonPodResumeState>();
        foreach (var id in ids)
        {
            var pod = Get(id);
            if (pod == null || pod.OperationId != operationId || pod.ParentShipId != recipient || pod.Phase != DungeonPodPhase.Docked || pod.ReturnAttempted || pod.Transport == null) return null;
            var attempted = new DungeonPodResumeState(pod.Id, pod.OperationId, pod.Phase, pod.PlayerOwned, true, false, pod.Transport.OutboundCrew, true, pod.ParentShipId, pod.Transport);
            next[id] = attempted; attempts.Add(attempted);
        }
        DungeonPodResumeCodec.Encode(next.Values); _pods = next; return attempts.AsReadOnly();
    }
    internal DungeonPodResumeState? BeginDockedRefund(Guid id)
    {
        var pod = Get(id);
        if (pod == null || pod.Phase != DungeonPodPhase.Docked || pod.ReturnAttempted || pod.Transport == null) return null;
        var attempted = new DungeonPodResumeState(pod.Id, pod.OperationId, pod.Phase, pod.PlayerOwned, true, false, pod.Transport.OutboundCrew, true, pod.ParentShipId, pod.Transport);
        Track(attempted); return attempted;
    }
    internal void Refunded(Guid id)
    {
        var pod = Get(id) ?? throw new InvalidOperationException("Unknown refund pod.");
        if (pod.Phase != DungeonPodPhase.Docked || !pod.ReturnAttempted || !pod.ReturnManifestKnown) throw new InvalidOperationException("Refund was not attempted.");
        Track(new(pod.Id, pod.OperationId, DungeonPodPhase.Refunded, pod.PlayerOwned, true, true, pod.ReturnCrew, true, pod.ParentShipId, pod.Transport));
    }
    internal DungeonPodResumeState? BeginReturn(Guid id)
    {
        var pod = Get(id); if (pod == null || !pod.CanRecover) return null;
        Track(new(pod.Id, pod.OperationId, pod.Phase, pod.PlayerOwned, true, false, pod.ReturnCrew, true, pod.ParentShipId, pod.Transport));
        return pod;
    }
    internal void Delivered(Guid id)
    {
        var pod = Get(id) ?? throw new InvalidOperationException("Unknown pod.");
        if (!pod.ReturnAttempted || !pod.ReturnManifestKnown) throw new InvalidOperationException("Return was not attempted with a known manifest.");
        Track(new(pod.Id, pod.OperationId, DungeonPodPhase.Arrived, pod.PlayerOwned, true, true, pod.ReturnCrew, true, pod.ParentShipId, pod.Transport));
    }
}

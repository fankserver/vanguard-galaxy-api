using System;

namespace VGModAPI.Core;

// Identity policy only: storage and canonical slot resolution are separate concerns.
internal sealed class SnapshotAssociation
{
    internal string Slot { get; }
    internal string VanillaHash { get; }
    internal string StateHash { get; }
    internal Guid Campaign { get; }
    internal Guid Snapshot { get; }

    internal SnapshotAssociation(string slot, string vanillaHash, string stateHash, Guid campaign, Guid snapshot)
    {
        if (string.IsNullOrWhiteSpace(slot)) throw new ArgumentException("Canonical slot required.", nameof(slot));
        if (!IsHash(vanillaHash) || !IsHash(stateHash)) throw new ArgumentException("Canonical SHA-256 required.");
        if (campaign == Guid.Empty || snapshot == Guid.Empty) throw new ArgumentException("Nonempty identities required.");
        Slot = slot; VanillaHash = vanillaHash; StateHash = stateHash; Campaign = campaign; Snapshot = snapshot;
    }

    private static bool IsHash(string value)
    {
        if (value == null || value.Length != 64) return false;
        foreach (var c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
        return true;
    }

    internal bool Matches(string slot, string vanillaHash) => Slot == slot && VanillaHash == vanillaHash;

    // The snapshot token may differ on a retry; unchanged content reuses the original association.
    internal bool CanReuse(SnapshotAssociation candidate) => Matches(candidate.Slot, candidate.VanillaHash)
        && Campaign == candidate.Campaign && StateHash == candidate.StateHash;
}

internal enum IdentityLoadDisposition { Restore, Isolated, Blocked }

internal static class PersistenceIdentity
{
    internal static IdentityLoadDisposition Resolve(string slot, string vanillaHash, SnapshotAssociation? association, bool metadataInvalid)
    {
        if (metadataInvalid) return IdentityLoadDisposition.Blocked;
        return association != null && association.Matches(slot, vanillaHash)
            ? IdentityLoadDisposition.Restore : IdentityLoadDisposition.Isolated;
    }

    internal static SnapshotAssociation Fork(SnapshotAssociation source, string destination, string actualVanillaHash, string actualStateHash, Guid campaign, Guid snapshot)
    {
        if (source.VanillaHash != actualVanillaHash || source.StateHash != actualStateHash) throw new InvalidOperationException("Imported bytes do not match source generation.");
        if (source.Slot == destination) throw new InvalidOperationException("Fork requires a distinct destination slot.");
        if (campaign == source.Campaign || snapshot == source.Snapshot) throw new InvalidOperationException("Fork requires fresh identities.");
        return new SnapshotAssociation(destination, actualVanillaHash, source.StateHash, campaign, snapshot);
    }
}

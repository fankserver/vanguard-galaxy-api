using System;
using System.IO;

namespace VGModAPI.Core;

internal sealed class MissionIdentityPersistence : IDisposable
{
    internal const string Owner = "vgmodapi.mission-identities";
    internal readonly MissionSerializationTracker Snapshots = new();
    private readonly IPersistenceRegistration _registration;
    private Guid? _restoredFor;
    private MissionIdentityRecord[] _records = Array.Empty<MissionIdentityRecord>();
    internal MissionIdentityPersistence(IPersistenceApi persistence, Func<bool> available)
    {
        _registration = persistence.Register(new PersistenceProvider(Owner, 1, () =>
            {
                if (!available()) throw new InvalidDataException("Mission identity observation unavailable.");
                return Snapshots.CaptureForStore();
            },
            (session, bytes) => { _records = bytes == null ? Array.Empty<MissionIdentityRecord>() : MissionIdentitySnapshot.Decode(bytes); _restoredFor = session.Id; }, Validate));
    }
    private static bool Validate(byte[] bytes)
    {
        try { MissionIdentitySnapshot.Decode(bytes); return true; }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
    }
    internal void Reset()
    { Snapshots.Reset(); _records = Array.Empty<MissionIdentityRecord>(); _restoredFor = null; }
    internal void Seed(MissionTransitions events, Guid session, object[] missions, string[] fingerprints)
    {
        if (missions.Length != fingerprints.Length) throw new InvalidDataException("Mission restoration membership mismatch.");
        var matches = _restoredFor == session ? MissionIdentitySnapshot.MatchUnique(_records, fingerprints) : new Guid?[missions.Length];
        for (int i = 0; i < missions.Length; i++)
            events.SeedIdentity(missions[i], matches[i], matches[i].HasValue ? MissionIdentityEvidence.SavedSnapshotMatch :
                _restoredFor == session ? MissionIdentityEvidence.MissingOrAmbiguous : MissionIdentityEvidence.Unavailable);
    }
    public void Dispose() { Reset(); _registration.Dispose(); }
}

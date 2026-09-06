using System;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class MissionAdapter
{
    private MissionIdentityPersistence? _identity;
    private MissionJsonBindings? _json;
    internal void EnableIdentity(IPersistenceApi persistence, MissionJsonBindings json)
    {
        _hub.CheckThread();
        if (_identity != null || _session.HasValue) throw new InvalidOperationException("Mission identity registration must precede sessions.");
        _json = json; _identity = new MissionIdentityPersistence(persistence, () => !_faulted && !_disposed && Current, detail =>
        {
            var capability = _hub.Capabilities.FirstOrDefault(c => c.Name == "save-data");
            if (capability != null) _hub.SetCapability(capability.Name, capability.Available, detail);
        });
    }
    internal void DisableIdentity()
    { _hub.CheckThread(); _identity?.Dispose(); _identity = null; _json = null; }
    internal MissionSerializationTracker.Capture? BeginSerialization()
    {
        if (_identity == null || !Current) return null;
        return _identity.Snapshots.Begin(_bindings.SerializationMembers(_player!).Select(row => row.Mission), Events.SnapshotIdentity, Events.Revision);
    }
    internal void EndSerialization(MissionSerializationTracker.Capture capture, object root)
    {
        if (_identity == null || !Current) return;
        var fingerprints = _json!.SavedFingerprints(root);
        _identity.Snapshots.Complete(capture, root, _bindings.SerializationMembers(_player!).Select(row => row.Mission).ToArray(), fingerprints, Events.Revision);
    }
    internal MissionSerializationTracker.StoreScope? BeginIdentityStore(object root) => _identity?.Snapshots.BeginStore(root, () => _json!.SavedFingerprints(root));
    private void SeedRestoredIdentities()
    {
        if (_identity == null) return;
        long revision = Events.Revision;
        var members = _bindings.SerializationMembers(_player!);
        var fingerprints = members.Select(row => _json!.CurrentFingerprint(row.Container, row.Mission)).ToArray();
        var after = _bindings.SerializationMembers(_player!);
        if (!Current || revision != Events.Revision || after.Length != members.Length || !after.Zip(members, (a, b) => ReferenceEquals(a.Mission, b.Mission) && a.Container == b.Container).All(equal => equal))
            throw new InvalidOperationException("Mission membership changed during identity restoration.");
        _identity.Seed(Events, _session!.Value, members.Select(row => row.Mission).ToArray(), fingerprints);
    }
}

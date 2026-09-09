using System;
using System.Linq;

namespace VGModAPI.Core.Integration;

/// <summary>Read-only same-owner world dependency checks after ordered reconstruction, including PlayerReady dispatch.</summary>
internal sealed class WorldReferenceResolver
{
    private readonly LifecycleHub _hub;
    private readonly WorldCreationCoordinator _creation;
    private readonly WorldDefinitionRegistry _definitions;
    private readonly WorldPersistenceBindings _persistence;
    private readonly WorldLifetimeHookHost _lifetime;
    internal WorldReferenceResolver(LifecycleHub hub, WorldCreationCoordinator creation, WorldDefinitionRegistry definitions,
        WorldPersistenceBindings persistence, WorldLifetimeHookHost lifetime)
    { _hub = hub; _creation = creation; _definitions = definitions; _persistence = persistence; _lifetime = lifetime; }
    internal bool? Knows(string owner, string nativeId)
    {
        _hub.CheckThread();
        try
        {
            var session = _hub.CurrentSession?.Id ?? Guid.Empty;
            if (session == Guid.Empty || !_creation.Restored(session) || !_persistence.StateReady(session)) return null;
            long revision = _creation.Revision;
            var record = _creation.Snapshot().SingleOrDefault(item => item.Identity.NativeId == nativeId);
            if (record == null || record.Identity.Owner != owner) return false;
            if (!_definitions.MatchesRetained(record.Definition) || !_lifetime.AllowUse(record.Native) ||
                !_persistence.StateReady(session) || !_definitions.MatchesRetained(record.Definition) ||
                _creation.Revision != revision || _hub.CurrentSession?.Id != session) return null;
            return ReferenceEquals(_creation.Find(session, record.Identity, observed: true), record);
        }
        catch (Exception) { return null; }
    }
}

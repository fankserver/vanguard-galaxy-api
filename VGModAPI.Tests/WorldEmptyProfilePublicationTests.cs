using System;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;
public sealed class WorldEmptyProfilePublicationTests
{
    [Fact]
    public void ProfileIsCheckedAfterRestorationWritesAndBeforeSnapshotReturn()
    {
        bool empty = true, rolledBack = false;
        var coordinator = new WorldCreationCoordinator(null!, () => { }, profile: _ =>
        { if (!empty) throw new InvalidDataException("Unsupported profile state."); });
        var session = Guid.NewGuid(); coordinator.Reset(session);
        var identity = new WorldObjectIdentity(new ContentDeclaration("author.a", "PoiX", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), Guid.NewGuid());
        var row = new WorldSnapshotInstance(new object(), identity, "system", new WorldSavedDefinition(identity.Owner, new WorldCombatDefinition(identity.LocalId, 1, "Site", "player", 1)));
        Assert.Throws<InvalidDataException>(() => coordinator.TryRestorePrepared(session, () => new WorldRestorationPlan(new[] { row },
            () => empty = false, () => { empty = true; rolledBack = true; })));
        Assert.True(rolledBack); Assert.False(coordinator.Restored(session));
        Assert.True(coordinator.TryRestore(session, () => new[] { row })); Assert.Single(coordinator.Snapshot());
        empty = false; Assert.Throws<InvalidDataException>(() => coordinator.Snapshot());
    }
}

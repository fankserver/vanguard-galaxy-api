using System;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PersistenceIdentityTests
{
    private static string Hash(char c) => new string(c, 64);
    private static SnapshotAssociation State(string slot, char vanilla, char progress, Guid? campaign = null)
        => new(slot, Hash(vanilla), Hash(progress), campaign ?? Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void SlotsDoNotShareEvenIdenticalVanillaBytes()
    {
        var a = State("slot-a", 'a', '1');
        Assert.Equal(IdentityLoadDisposition.Isolated, PersistenceIdentity.Resolve("slot-b", Hash('a'), a, false));
    }

    [Fact]
    public void RollbackSelectsExactGenerationNotLatestProgress()
    {
        var old = State("autosave-0", 'a', '1');
        var latest = State("autosave-0", 'b', '2', old.Campaign);
        var selected = new[] { latest, old }.Single(s => s.Matches("autosave-0", Hash('a')));
        Assert.Equal(Hash('1'), selected.StateHash);
        Assert.Equal(IdentityLoadDisposition.Isolated, PersistenceIdentity.Resolve("autosave-0", Hash('a'), latest, false));
    }

    [Fact]
    public void IdenticalVanillaWithDifferentProgressOrCampaignCannotReplaceAssociation()
    {
        var original = State("slot", 'a', '1');
        Assert.False(original.CanReuse(State("slot", 'a', '2', original.Campaign)));
        Assert.False(original.CanReuse(State("slot", 'a', '1')));
        Assert.True(original.CanReuse(State("slot", 'a', '1', original.Campaign)));
    }

    [Fact]
    public void SaveAsCanRetainCampaignWithoutSharingSlotBinding()
    {
        var source = State("manual", 'a', '1');
        var destination = State("autosave-1", 'b', '1', source.Campaign);
        Assert.Equal(source.Campaign, destination.Campaign);
        Assert.NotEqual(source.Snapshot, destination.Snapshot);
        Assert.False(destination.Matches(source.Slot, source.VanillaHash));
    }

    [Fact]
    public void MissingMetadataIsDistinctFromCorruptOrUnsupportedMetadata()
    {
        Assert.Equal(IdentityLoadDisposition.Isolated, PersistenceIdentity.Resolve("slot", Hash('a'), null, false));
        Assert.Equal(IdentityLoadDisposition.Blocked, PersistenceIdentity.Resolve("slot", Hash('a'), null, true));
        var valid = State("slot", 'a', '1');
        Assert.Equal(IdentityLoadDisposition.Restore, PersistenceIdentity.Resolve("slot", Hash('a'), valid, false));
        Assert.Equal(IdentityLoadDisposition.Blocked, PersistenceIdentity.Resolve("slot", Hash('a'), valid, true));
    }

    [Fact]
    public void ExplicitImportRequiresExactBytesAndFreshIdentity()
    {
        var source = State("source", 'a', '1');
        var fork = PersistenceIdentity.Fork(source, "destination", Hash('a'), Hash('1'), Guid.NewGuid(), Guid.NewGuid());
        Assert.NotEqual(source.Campaign, fork.Campaign);
        Assert.Equal(source.StateHash, fork.StateHash);
        Assert.Throws<InvalidOperationException>(() => PersistenceIdentity.Fork(source, "destination", Hash('b'), Hash('1'), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => PersistenceIdentity.Fork(source, "destination", Hash('a'), Hash('1'), source.Campaign, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => PersistenceIdentity.Fork(source, "destination", Hash('a'), Hash('2'), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => PersistenceIdentity.Fork(source, "source", Hash('a'), Hash('1'), Guid.NewGuid(), Guid.NewGuid()));
    }
}

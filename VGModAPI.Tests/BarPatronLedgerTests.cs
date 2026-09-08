using System;
using System.IO;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronLedgerTests
{
    private static BarPatronState Row(string owner, string name = "Original", string local = "contact") =>
        new(new BarPatronId(owner, local), "station", name, "Description", "seed");

    [Fact]
    public void RepeatedOwnerWritesDoNotDuplicatePatronsAndCannotAffectOtherOwners()
    {
        var ledger = new BarPatronLedger();
        Assert.True(ledger.TryPut("a", Row("a")));
        Assert.True(ledger.TryPut("b", Row("b")));
        Assert.True(ledger.TryPut("a", Row("a", "Changed")));
        Assert.Equal(2, ledger.Snapshot().Count);
        var before = ledger.Capture();
        Assert.False(ledger.TryPut("b", Row("a", "Wrong owner")));
        Assert.False(ledger.TryRemove("b", new BarPatronId("a", "contact")));
        Assert.Equal(before, ledger.Capture());
    }

    [Fact]
    public void OlderSnapshotRestorationDoesNotLeakNewerPatronsOrEdits()
    {
        var ledger = new BarPatronLedger();
        Assert.True(ledger.TryPut("a", Row("a")));
        var older = ledger.Capture();
        Assert.True(ledger.TryPut("a", Row("a", "Changed")));
        Assert.True(ledger.TryPut("b", Row("b")));
        ledger.Restore(older);
        Assert.Equal("Original", Assert.Single(ledger.Snapshot()).Name);
        Assert.Equal(older, ledger.Capture());
        ledger.Restore(older);
        Assert.Single(ledger.Snapshot());
    }

    [Fact]
    public void MalformedRestoreAndQuotaRefusalDoNotPartiallyReplaceState()
    {
        var ledger = new BarPatronLedger();
        for (int index = 0; index < BarPatronCodec.MaxPerProvider; index++)
            Assert.True(ledger.TryPut("a", Row("a", local: "contact-" + index)));
        var before = ledger.Capture();
        Assert.False(ledger.TryPut("a", Row("a", local: "overflow")));
        Assert.Equal(before, ledger.Capture());
        Assert.Throws<InvalidDataException>(() => ledger.Restore(Array.Empty<byte>()));
        Assert.Equal(before, ledger.Capture());
    }
}

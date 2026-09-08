using System;
using System.IO;
using System.Collections.Generic;
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
    public void EveryProviderCanFillItsIsolatedShareAfterOtherProvidersAreFull()
    {
        var all = new List<BarPatronState>();
        var ledger = new BarPatronLedger();
        BarPatronState? last = null;
        for (int owner = 0; owner < BarPatronCodec.MaxProviders; owner++)
        {
            string provider = "a" + owner.ToString("D2");
            var rows = new List<BarPatronState>();
            for (int index = 0; index < 7; index++)
                rows.Add(new BarPatronState(new BarPatronId(provider, "p" + index), "s", "n", new string('d', 1024), "z"));
            var minimal = new BarPatronState(new BarPatronId(provider, "p7"), "s", "n", "d", "z");
            int overhead = BarPatronCodec.Encode(new[] { minimal }).Length - BarPatronCodec.HeaderBytes - 1;
            int remaining = BarPatronCodec.ProviderBytes - (BarPatronCodec.Encode(rows).Length - BarPatronCodec.HeaderBytes) - overhead;
            Assert.InRange(remaining, 1, 1023);
            last = new BarPatronState(minimal.Id, "s", "n", new string('d', remaining), "z");
            rows.Add(last);
            Assert.Equal(BarPatronCodec.ProviderBytes + BarPatronCodec.HeaderBytes, BarPatronCodec.Encode(rows).Length);
            if (owner == BarPatronCodec.MaxProviders - 1)
            {
                ledger.Restore(BarPatronCodec.Encode(all));
                foreach (var row in rows) Assert.True(ledger.TryPut(provider, row));
            }
            all.AddRange(rows);
        }
        var before = ledger.Capture();
        Assert.Equal(BarPatronCodec.HeaderBytes + BarPatronCodec.MaxProviders * BarPatronCodec.ProviderBytes, before.Length);
        Assert.False(ledger.TryPut(last!.Id.Provider, new BarPatronState(last.Id, "s", "n", last.Description + "d", "z")));
        Assert.Equal(before, ledger.Capture());
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

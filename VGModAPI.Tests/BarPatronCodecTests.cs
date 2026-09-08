using System;
using System.IO;
using System.Linq;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatronCodecTests
{
    private static BarPatronState Row(string provider = "campaign", string local = "contact") =>
        new(new BarPatronId(provider, local), "CustomAct3RickoStation", "Élodie", "A contact", "fixed-seed",
            new StoryContentId(provider, "mission-x"), new Guid("01234567-1234-1234-1234-012345678901"));

    [Fact]
    public void CanonicalRoundTripRetainsOwnerStateAndMissionReference()
    {
        var rows = new[] { Row("jobs"), Row() };
        var bytes = BarPatronCodec.Encode(rows);
        Assert.Equal(bytes, BarPatronCodec.Encode(rows.Reverse()));
        var restored = BarPatronCodec.Decode(bytes);
        Assert.Equal(2, restored.Length);
        Assert.Equal(rows[1].Id, restored[0].Id);
        Assert.Equal(rows[1].Station, restored[0].Station);
        Assert.Equal(rows[1].Name, restored[0].Name);
        Assert.Equal(rows[1].Seed, restored[0].Seed);
        Assert.Equal(rows[1].Mission, restored[0].Mission);
        Assert.Equal(rows[1].Occurrence, restored[0].Occurrence);
        Assert.True(BarPatronCodec.Validate(bytes));
    }

    [Fact]
    public void DuplicateIdentitiesAndProviderCountLimitsRefuseWithoutTruncation()
    {
        Assert.Throws<InvalidDataException>(() => BarPatronCodec.Encode(new[] { Row(), Row() }));
        Assert.Throws<InvalidDataException>(() => BarPatronCodec.Encode(Enumerable.Range(0, 33).Select(index => Row(local: "contact-" + index))));
        Assert.Throws<InvalidDataException>(() => BarPatronCodec.Encode(Enumerable.Range(0, 33).Select(index => Row("provider-" + index))));
    }

    [Fact]
    public void ProviderPayloadLimitAppliesEvenBelowRecordCountLimit()
    {
        var rows = Enumerable.Range(0, 20).Select(index => new BarPatronState(new BarPatronId("campaign", "contact-" + index),
            "station", "name", new string('d', 1024), "seed"));
        Assert.Throws<InvalidDataException>(() => BarPatronCodec.Encode(rows));
    }

    [Fact]
    public void TruncationTrailingBytesAndFutureSchemasAreNotFreshEmptyState()
    {
        var bytes = BarPatronCodec.Encode(new[] { Row() });
        for (int length = 0; length < bytes.Length; length++) Assert.False(BarPatronCodec.Validate(bytes.Take(length).ToArray()));
        Assert.False(BarPatronCodec.Validate(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[4] = 2;
        Assert.False(BarPatronCodec.Validate(bytes));
        Assert.Empty(BarPatronCodec.Decode(BarPatronCodec.Encode(Array.Empty<BarPatronState>())));
    }
}

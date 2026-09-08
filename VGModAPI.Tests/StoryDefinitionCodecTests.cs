using System;
using System.IO;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryDefinitionCodecTests
{
    [Fact]
    public void GeneratedDefinitionRoundTripsAllSupportedAuthorData()
    {
        var definition = new StoryMissionDefinition("generated", "Generated title", "Generated description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Travel", new[] { StoryObjective.TravelTo("original-poi", 15).WithKey("visit"), StoryObjective.CollectCredits(83) }, false) },
            new[] { new StoryReward(StoryRewardKind.Credits, 17) }, StoryDifficulty.Hard, StoryRetention.Campaign, false,
            "category", "completion", new[] { "choice" });
        var bytes = StoryDefinitionCodec.Encode(definition);
        var restored = StoryDefinitionCodec.Decode(bytes);
        Assert.Equal(bytes, StoryDefinitionCodec.Encode(restored));
        Assert.Equal("original-poi", restored.Steps[0].Objectives[0].TargetPoiId);
        Assert.Equal("Generated title", restored.Title);
        Assert.Equal(17, restored.Rewards[0].Amount);
        Assert.False(restored.CanAbandon);
        Assert.False(restored.Steps[0].RequireAllObjectives);
    }

    [Fact]
    public void ScriptedRevisionAndLocalizedDescriptionRoundTrip()
    {
        var definition = new StoryMissionDefinition("campaign", "Title", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Dialogue", new[] { StoryObjective.Scripted("answer", "Réponse", 3) }) }).WithRevision(2, 1);
        var bytes = StoryDefinitionCodec.Encode(definition);
        var restored = StoryDefinitionCodec.Decode(bytes);
        Assert.Equal(bytes, StoryDefinitionCodec.Encode(restored));
        Assert.Equal(2, restored.ContentRevision);
        Assert.Equal(1, restored.MigratesFromRevision);
        Assert.Equal("Réponse", restored.Steps[0].Objectives[0].Description);
    }

    [Fact]
    public void NonScriptedRevisionIsNormalizedToInvalidData()
    {
        var definition = new StoryMissionDefinition("job", "Title", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Travel", new[] { StoryObjective.TravelTo("poi") }) });
        var bytes = StoryDefinitionCodec.Encode(definition);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        reader.ReadByte();
        for (int index = 0; index < 6; index++)
        {
            int length = reader.ReadInt32();
            if (length >= 0) stream.Position += length;
        }
        stream.Position += 3;
        int revisionOffset = (int)stream.Position;
        var state = StoryStateCodec.Encode(new[] { new StoryOccurrenceEntry(new StoryContentId("author", "job"), Guid.NewGuid(),
            StoryRetention.Temporary, 1, retainedDefinition: definition) });
        int payloadOffset = -1;
        for (int index = 0; index <= state.Length - bytes.Length; index++)
            if (state.AsSpan(index, bytes.Length).SequenceEqual(bytes)) { payloadOffset = index; break; }
        Assert.True(payloadOffset >= 0);
        state[payloadOffset + revisionOffset] = 2;
        Assert.False(StoryStateCodec.Validate(state));
        bytes[revisionOffset] = 2;
        Assert.Throws<InvalidDataException>(() => StoryDefinitionCodec.Decode(bytes));
    }

    [Fact]
    public void OversizedTruncatedAndExtendedPayloadsAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => StoryDefinitionCodec.Decode(new byte[StoryDefinitionCodec.MaxBytes + 1]));
        var definition = new StoryMissionDefinition("job", "Title", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Travel", new[] { StoryObjective.TravelTo("poi") }) });
        var bytes = StoryDefinitionCodec.Encode(definition);
        for (int length = 0; length < bytes.Length; length++)
        {
            var prefix = new byte[length];
            Array.Copy(bytes, prefix, length);
            Assert.ThrowsAny<Exception>(() => StoryDefinitionCodec.Decode(prefix));
        }
        Array.Resize(ref bytes, bytes.Length + 1);
        Assert.Throws<InvalidDataException>(() => StoryDefinitionCodec.Decode(bytes));
    }
}

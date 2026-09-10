using System;
using System.IO;
using System.Linq;
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
            new[] { StoryReward.Credits(17) }, StoryDifficulty.Hard, StoryRetention.Campaign, false,
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
    public void DeliveryObjectivesAndReputationRewardsRoundTrip()
    {
        var definition = new StoryMissionDefinition("delivery", "Title", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Deliver", new[] { StoryObjective.DeliverItems("UmbralMetafiber", 1, "station-poi").WithKey("deliver") }) },
            new[] { StoryReward.Reputation(600), StoryReward.Reputation(50, new StoryFactionId("MiningGuild")), StoryReward.Credits(5) });
        var bytes = StoryDefinitionCodec.Encode(definition);
        var restored = StoryDefinitionCodec.Decode(bytes);
        Assert.Equal(bytes, StoryDefinitionCodec.Encode(restored));
        var objective = restored.Steps[0].Objectives[0];
        Assert.Equal(StoryObjectiveKind.DeliverItems, objective.Kind);
        Assert.Equal("UmbralMetafiber", objective.ItemTypeId);
        Assert.Equal("station-poi", objective.TargetPoiId);
        Assert.Equal(1, objective.RequiredAmount);
        Assert.Null(restored.Rewards[0].Faction);
        Assert.Equal("MiningGuild", restored.Rewards[1].Faction!.Value.Value);
        // Trailing bytes are refused, never adapted.
        Assert.ThrowsAny<InvalidDataException>(() => StoryDefinitionCodec.Decode(bytes.Concat(new byte[] { 1 }).ToArray()));
        // Duplicate reputation entries per DISTINCT faction are allowed; same-faction duplicates are not.
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("x", "T", "D", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("s", new[] { StoryObjective.CollectCredits(1) }) },
            new[] { StoryReward.Reputation(1), StoryReward.Reputation(2) }));
        // An explicit source-faction grant is the same grant as the null (source) form.
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("x", "T", "D", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("s", new[] { StoryObjective.CollectCredits(1) }) },
            new[] { StoryReward.Reputation(1), StoryReward.Reputation(2, new StoryFactionId("TradingGuild")) }));
    }

    [Fact]
    public void LegacyVersion1PayloadsRemainReadable()
    {
        // A retained pre-delivery definition (schema 1) decodes exactly as before.
        var legacyShaped = new StoryMissionDefinition("legacy", "Title", "Description", new StoryFactionId("TradingGuild"),
            new[] { new StoryStep("Travel", new[] { StoryObjective.TravelTo("poi", 5).WithKey("visit") }) },
            new[] { StoryReward.Credits(17) });
        var v2 = StoryDefinitionCodec.Encode(legacyShaped);
        var v1 = DowngradeToVersion1(v2);
        var restored = StoryDefinitionCodec.Decode(v1);
        Assert.Equal("poi", restored.Steps[0].Objectives[0].TargetPoiId);
        Assert.Equal(17, restored.Rewards[0].Amount);
    }

    /// <summary>Strips the version-2 additions (per-objective item id, per-reward faction) back to the v1 wire shape.</summary>
    private static byte[] DowngradeToVersion1(byte[] v2)
    {
        // v2 differs from v1 only by a trailing Text(null) (-1 int) after each objective and each reward.
        using var input = new MemoryStream(v2, false);
        using var reader = new BinaryReader(input);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write((byte)1); Assert.Equal(2, reader.ReadByte());
        for (int text = 0; text < 6; text++) CopyText(reader, writer);
        writer.Write(reader.ReadBytes(2)); writer.Write(reader.ReadBoolean());
        writer.Write(reader.ReadInt32()); writer.Write(reader.ReadInt32());
        int steps = reader.ReadByte(); writer.Write((byte)steps);
        for (int step = 0; step < steps; step++)
        {
            CopyText(reader, writer); writer.Write(reader.ReadBoolean());
            int objectives = reader.ReadByte(); writer.Write((byte)objectives);
            for (int objective = 0; objective < objectives; objective++)
            {
                writer.Write(reader.ReadByte()); CopyText(reader, writer); CopyText(reader, writer);
                writer.Write(reader.ReadInt32()); writer.Write(reader.ReadSingle()); CopyText(reader, writer);
                Assert.Equal(-1, reader.ReadInt32()); // drop the v2 item id slot
            }
        }
        int rewards = reader.ReadByte(); writer.Write((byte)rewards);
        for (int reward = 0; reward < rewards; reward++)
        {
            writer.Write(reader.ReadByte()); writer.Write(reader.ReadInt32());
            Assert.Equal(-1, reader.ReadInt32()); // drop the v2 faction slot
        }
        writer.Write((byte)input.ReadByte()); // choice count
        input.CopyTo(output);
        writer.Flush();
        return output.ToArray();
    }
    private static void CopyText(BinaryReader reader, BinaryWriter writer)
    {
        int size = reader.ReadInt32(); writer.Write(size);
        if (size > 0) writer.Write(reader.ReadBytes(size));
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

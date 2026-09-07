using System;
using System.IO;
using System.Linq;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryObjectiveIdentityTests
{
    [Fact]
    public void KeyedCopyDoesNotMutateOriginalObjective()
    {
        var original = StoryObjective.TravelTo("poi");
        var keyed = original.WithKey("rendezvous");
        Assert.Null(original.LocalKey);
        Assert.Equal("rendezvous", keyed.LocalKey);
        Assert.Equal(original.TargetPoiId, keyed.TargetPoiId);
        Assert.Throws<ArgumentException>(() => original.WithKey("Display Name"));
    }

    [Fact]
    public void DuplicateKeysAcrossDifferentStepsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new StoryMissionDefinition("mission-x", "Title", "Description",
            new StoryFactionId("Civilian"), new[]
            {
                new StoryStep("First", new[] { StoryObjective.TravelTo("a").WithKey("visit") }),
                new StoryStep("Second", new[] { StoryObjective.TravelTo("b").WithKey("visit") })
            }));
    }

    [Fact]
    public void ReorderedStepsResolveByKeyRatherThanDescription()
    {
        StoryMissionDefinition Definition(params StoryStep[] steps) => new("mission-x", "Title", "Description", new StoryFactionId("Civilian"), steps);
        var oldLayout = new StoryObjectiveLayout(Definition(
            new StoryStep("Same label", new[] { StoryObjective.TravelTo("a").WithKey("visit") }),
            new StoryStep("Same label", new[] { StoryObjective.CollectCredits(5).WithKey("earn") })));
        var newLayout = new StoryObjectiveLayout(Definition(
            new StoryStep("Localized", new[] { StoryObjective.CollectCredits(5).WithKey("earn") }),
            new StoryStep("Edited", new[] { StoryObjective.TravelTo("a").WithKey("visit") })));
        Assert.True(oldLayout.TryMapTo(newLayout, out var mapping));
        Assert.Equal(2, mapping.Count);
        Assert.True(newLayout.TryResolve("visit", out var slot));
        Assert.Equal(1, slot.Step);
        var incompatible = new StoryObjectiveLayout(Definition(new StoryStep("Same label",
            new[] { StoryObjective.CollectCredits(5).WithKey("visit") })));
        Assert.False(oldLayout.TryMapTo(incompatible, out var refused));
        Assert.Empty(refused);
    }

    [Fact]
    public void OccurrenceCodecPersistsLayoutAndCountsItAgainstAdmissionBudget()
    {
        var definition = new StoryContentId("campaign", "mission-x");
        var layout = new StoryObjectiveLayout(new[] { new StoryObjectiveLayout.Slot("visit", 1, 2, StoryObjectiveKind.TravelToPoi) });
        var ledger = new StoryLedger();
        var occurrence = Guid.NewGuid();
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.Offer(definition, StoryRetention.Campaign, occurrence, 0, out _, layout));
        var bytes = StoryStateCodec.Encode(ledger.Entries);
        var restored = Assert.Single(StoryStateCodec.Decode(bytes));
        Assert.Equal(occurrence, restored.OccurrenceId);
        Assert.True(restored.ObjectiveLayout.SamePositions(layout));
        var empty = new StoryOccurrenceEntry(definition, occurrence, StoryRetention.Campaign, 1);
        Assert.Equal(23, StoryStateCodec.EncodedSize(restored) - StoryStateCodec.EncodedSize(empty));
        var legacy = StoryStateCodec.Encode(new[] { empty });
        Array.Copy(BitConverter.GetBytes(2), 0, legacy, 4, 4);
        Assert.Empty(Assert.Single(StoryStateCodec.Decode(legacy)).ObjectiveLayout.Slots);

        var fullLayout = new StoryObjectiveLayout(Enumerable.Range(0, StoryObjectiveLayout.MaxSlots)
            .Select(index => new StoryObjectiveLayout.Slot("objective-" + index + new string('x', 32), index / 8, index % 8, StoryObjectiveKind.TravelToPoi)));
        var bounded = new StoryLedger();
        int admitted = 0;
        while (bounded.Offer(definition, StoryRetention.Campaign, Guid.NewGuid(), 0, out _, fullLayout) == StoryLedgerStatus.Accepted) admitted++;
        Assert.InRange(admitted, 1, 10);
        Assert.Equal(admitted, bounded.Entries.Count());
        Assert.True(StoryStateCodec.Validate(StoryStateCodec.Encode(bounded.Entries)));
    }

    [Fact]
    public void DeclaredRevisionMigrationPreservesProgressByKeyAcrossStepReordering()
    {
        var source = new StoryObjectiveLayout(new[] {
            new StoryObjectiveLayout.Slot("talk", 0, 0, StoryObjectiveKind.Scripted, 5, 2),
            new StoryObjectiveLayout.Slot("report", 1, 0, StoryObjectiveKind.Scripted, 1) });
        var definition = new StoryMissionDefinition("mission-x", "Title", "Description", new StoryFactionId("TradingGuild"), new[] {
            new StoryStep("Localized report", new[] { StoryObjective.Scripted("report", "Report") }),
            new StoryStep("Edited dialogue", new[] { StoryObjective.Scripted("talk", "Talk", 5) }) });
        Assert.False(source.TryMigrate(definition.WithRevision(2), out _));
        Assert.True(source.TryMigrate(definition.WithRevision(2, 1), out var migrated));
        Assert.Equal(2, migrated.Revision);
        Assert.True(migrated.TryResolve("talk", out var talk));
        Assert.Equal(1, talk.Step);
        Assert.Equal(2, talk.Progress);
        Assert.Equal(1, source.Revision);
        Assert.Equal(0, source.Slots.Single(slot => slot.Key == "talk").Step);
    }

    [Fact]
    public void VerifiedRetryResetsProgressWithoutChangingObjectiveIdentity()
    {
        var ledger = new StoryLedger();
        var id = new StoryContentId("campaign", "mission-x");
        var occurrence = Guid.NewGuid();
        var layout = new StoryObjectiveLayout(new[] { new StoryObjectiveLayout.Slot("beat", 0, 0, StoryObjectiveKind.Scripted, 5) });
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.Offer(id, StoryRetention.Campaign, occurrence, 0, out _, layout));
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.Activate(id, occurrence, out _));
        Assert.True(ledger.TryGet(occurrence, out var entry));
        entry.SetObjectiveProgress("beat", 3);
        var beforeRetry = StoryStateCodec.Encode(ledger.Entries);
        Assert.Equal(StoryLedgerStatus.Accepted, ledger.ClearFailure(id, occurrence, out _));
        Assert.Equal(0, Assert.Single(entry.ObjectiveLayout.Slots).Progress);
        Assert.Equal("beat", Assert.Single(entry.ObjectiveLayout.Slots).Key);
        Assert.Equal(occurrence, entry.OccurrenceId);
        Assert.Equal(3, Assert.Single(Assert.Single(StoryStateCodec.Decode(beforeRetry)).ObjectiveLayout.Slots).Progress);
    }

    [Fact]
    public void PartialProgressIsAbsoluteIdempotentAndSurvivesCodecRollback()
    {
        var layout = new StoryObjectiveLayout(new[] { new StoryObjectiveLayout.Slot("beat", 0, 0, StoryObjectiveKind.Scripted, 5) });
        var partial = layout.WithProgress("beat", 2).WithProgress("beat", 2);
        Assert.Equal(2, Assert.Single(partial.Slots).Progress);
        Assert.Equal(0, Assert.Single(layout.Slots).Progress);
        Assert.Throws<ArgumentOutOfRangeException>(() => partial.WithProgress("beat", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => partial.WithProgress("beat", 6));
        var id = new StoryContentId("campaign", "mission-x");
        var occurrence = Guid.NewGuid();
        byte[] Capture(StoryObjectiveLayout value) => StoryStateCodec.Encode(new[] {
            new StoryOccurrenceEntry(id, occurrence, StoryRetention.Campaign, 1, objectiveLayout: value) });
        var older = Capture(partial);
        Assert.Equal(5, Assert.Single(Assert.Single(StoryStateCodec.Decode(Capture(partial.WithProgress("beat", 5)))).ObjectiveLayout.Slots).Progress);
        Assert.Equal(2, Assert.Single(Assert.Single(StoryStateCodec.Decode(older)).ObjectiveLayout.Slots).Progress);
    }

    [Fact]
    public void LayoutCodecIsBoundedAndPreservesStablePositions()
    {
        var layout = new StoryObjectiveLayout(new[] { new StoryObjectiveLayout.Slot("visit", 1, 2, StoryObjectiveKind.TravelToPoi) });
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) StoryObjectiveLayoutCodec.Write(writer, layout);
        Assert.Equal(StoryObjectiveLayoutCodec.EncodedSize(layout), stream.Length);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var restored = StoryObjectiveLayoutCodec.Read(reader);
        Assert.True(restored.TryResolve("visit", out var slot));
        Assert.Equal(1, slot.Step);
        Assert.Equal(2, slot.Objective);
        Assert.Throws<ArgumentException>(() => new StoryObjectiveLayout(new[] {
            new StoryObjectiveLayout.Slot("a", 0, 0, StoryObjectiveKind.TravelToPoi),
            new StoryObjectiveLayout.Slot("b", 0, 0, StoryObjectiveKind.TravelToPoi) }));
        using var oversized = new BinaryReader(new MemoryStream(new byte[] { 65 }));
        Assert.Throws<InvalidDataException>(() => StoryObjectiveLayoutCodec.Read(oversized));
    }

    [Fact]
    public void OwnerOccurrenceAndKeyAllParticipateInIdentity()
    {
        var occurrence = Guid.NewGuid();
        var definition = new StoryContentId("campaign", "mission-x");
        var id = new StoryObjectiveId(definition, occurrence, "visit");
        Assert.Equal(id, new StoryObjectiveId(definition, occurrence, "visit"));
        Assert.NotEqual(id, new StoryObjectiveId(new StoryContentId("job", "mission-x"), occurrence, "visit"));
        Assert.NotEqual(id, new StoryObjectiveId(definition, Guid.NewGuid(), "visit"));
        Assert.NotEqual(id, new StoryObjectiveId(definition, occurrence, "report"));
        Assert.Throws<ArgumentException>(() => new StoryObjectiveId(definition, Guid.Empty, "visit"));
    }
}

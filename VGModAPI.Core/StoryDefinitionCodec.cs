using System;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Bounded immutable author data needed to rebuild an occurrence without a provider serializer.</summary>
internal static class StoryDefinitionCodec
{
    internal const int MaxBytes = StoryLedger.ProviderPayloadBudget;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool SameMetadata(StoryMissionDefinition? saved, StoryMissionDefinition next) => saved != null
        && saved.LocalId == next.LocalId && saved.Title == next.Title && saved.Description == next.Description
        && saved.SourceFaction.Equals(next.SourceFaction) && saved.Category == next.Category
        && saved.CompletionText == next.CompletionText && saved.Difficulty == next.Difficulty
        && saved.CanAbandon == next.CanAbandon && saved.Retention == next.Retention
        && saved.ChoiceKeys.SequenceEqual(next.ChoiceKeys)
        && saved.Rewards.Select(reward => (reward.Kind, reward.Amount, reward.Faction)).SequenceEqual(next.Rewards.Select(reward => (reward.Kind, reward.Amount, reward.Faction)));

    internal static byte[] Encode(StoryMissionDefinition definition)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        // Version 2 adds the delivery item identity per objective and the optional reward faction.
        writer.Write((byte)2);
        Text(writer, definition.LocalId); Text(writer, definition.Title); Text(writer, definition.Description);
        Text(writer, definition.SourceFaction.Value); Text(writer, definition.Category); Text(writer, definition.CompletionText);
        writer.Write((byte)definition.Difficulty); writer.Write((byte)definition.Retention); writer.Write(definition.CanAbandon);
        writer.Write(definition.ContentRevision); writer.Write(definition.MigratesFromRevision ?? 0);
        writer.Write((byte)definition.Steps.Count);
        foreach (var step in definition.Steps)
        {
            Text(writer, step.Description); writer.Write(step.RequireAllObjectives); writer.Write((byte)step.Objectives.Count);
            foreach (var objective in step.Objectives)
            {
                writer.Write((byte)objective.Kind); Text(writer, objective.LocalKey);
                Text(writer, objective.TargetPoiId); writer.Write(objective.RequiredAmount); writer.Write(objective.RequiredVisitSeconds);
                Text(writer, objective.Description); Text(writer, objective.ItemTypeId);
            }
        }
        writer.Write((byte)definition.Rewards.Count);
        foreach (var reward in definition.Rewards) { writer.Write((byte)reward.Kind); writer.Write(reward.Amount); Text(writer, reward.Faction?.Value); }
        writer.Write((byte)definition.ChoiceKeys.Count);
        foreach (var key in definition.ChoiceKeys) Text(writer, key);
        writer.Flush();
        if (stream.Length > MaxBytes) throw new InvalidDataException("Retained definition exceeds its bounded payload.");
        return stream.ToArray();
    }

    internal static StoryMissionDefinition Decode(byte[] payload)
    {
        if (payload == null || payload.Length > MaxBytes) throw new InvalidDataException("Invalid definition payload size.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Utf8);
        int version = reader.ReadByte();
        if (version != 1 && version != 2) throw new InvalidDataException("Unknown definition format.");
        var local = Required(reader); var title = Required(reader); var description = Required(reader);
        var faction = new StoryFactionId(Required(reader)); var category = Text(reader); var completion = Text(reader);
        var difficulty = (StoryDifficulty)reader.ReadByte(); var retention = (StoryRetention)reader.ReadByte(); var abandon = Boolean(reader);
        int revision = reader.ReadInt32(), from = reader.ReadInt32();
        var steps = new StoryStep[Count(reader, 1, StoryMissionDefinition.MaxSteps)];
        for (int index = 0; index < steps.Length; index++)
        {
            var label = Required(reader); bool all = Boolean(reader);
            var objectives = new StoryObjective[Count(reader, 1, StoryStep.MaxObjectives)];
            for (int item = 0; item < objectives.Length; item++)
            {
                var kind = (StoryObjectiveKind)reader.ReadByte(); var key = Text(reader); var target = Text(reader);
                int amount = reader.ReadInt32(); float visit = reader.ReadSingle(); var text = Text(reader);
                var itemType = version >= 2 ? Text(reader) : null;
                StoryObjective objective = kind switch
                {
                    StoryObjectiveKind.TravelToPoi when amount == 0 && text == null && itemType == null => StoryObjective.TravelTo(target!, visit),
                    StoryObjectiveKind.CollectCredits when target == null && visit == 0 && text == null && itemType == null => StoryObjective.CollectCredits(amount),
                    StoryObjectiveKind.KillEnemies when target == null && visit == 0 && text == null && itemType == null => StoryObjective.KillEnemies(amount),
                    StoryObjectiveKind.Scripted when target == null && visit == 0 && itemType == null => StoryObjective.Scripted(key!, text!, amount),
                    StoryObjectiveKind.DeliverItems when visit == 0 && text == null && itemType != null => StoryObjective.DeliverItems(itemType, amount, target!),
                    _ => throw new InvalidDataException("Invalid retained objective shape.")
                };
                objectives[item] = key == null ? objective : objective.WithKey(key);
            }
            steps[index] = new StoryStep(label, objectives, all);
        }
        var rewards = new StoryReward[Count(reader, 0, StoryMissionDefinition.MaxRewards)];
        for (int index = 0; index < rewards.Length; index++)
        {
            var kind = (StoryRewardKind)reader.ReadByte(); int amount = reader.ReadInt32();
            var rewardFaction = version >= 2 ? Text(reader) : null;
            if (rewardFaction != null && kind != StoryRewardKind.Reputation) throw new InvalidDataException("Invalid retained reward shape.");
            rewards[index] = kind switch
            {
                StoryRewardKind.Reputation => StoryReward.Reputation(amount, rewardFaction == null ? null : new StoryFactionId(rewardFaction)),
                StoryRewardKind.Credits => StoryReward.Credits(amount),
                StoryRewardKind.Experience => StoryReward.Experience(amount),
                _ => throw new InvalidDataException("Unknown retained reward kind.")
            };
        }
        var choices = new string[Count(reader, 0, StoryMissionDefinition.MaxChoiceKeys)];
        for (int index = 0; index < choices.Length; index++) choices[index] = Required(reader);
        if (stream.Position != stream.Length || revision < 1 || from < 0) throw new InvalidDataException("Invalid retained definition trailer.");
        var result = new StoryMissionDefinition(local, title, description, faction, steps, rewards, difficulty, retention, abandon, category, completion, choices);
        try { return revision == 1 && from == 0 ? result : result.WithRevision(revision, from == 0 ? null : from); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid retained revision shape.", error); }
    }

    private static void Text(BinaryWriter writer, string? value)
    {
        if (value == null) { writer.Write(-1); return; }
        var bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string? Text(BinaryReader reader)
    {
        int size = reader.ReadInt32();
        if (size == -1) return null;
        if (size < 0 || size > MaxBytes || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid retained text length.");
        return Utf8.GetString(reader.ReadBytes(size));
    }
    private static string Required(BinaryReader reader) => Text(reader) ?? throw new InvalidDataException("Missing retained text.");
    private static bool Boolean(BinaryReader reader) => reader.ReadByte() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Invalid retained boolean.") };
    private static int Count(BinaryReader reader, int min, int max)
    {
        int count = reader.ReadByte();
        return count >= min && count <= max ? count : throw new InvalidDataException("Invalid retained collection count.");
    }
}

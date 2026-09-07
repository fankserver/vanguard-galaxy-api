using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

/// <summary>
/// Bounded binary codec for the API-owned story state. It uses only netstandard binary IO and UTF-8;
/// no JSON library is introduced, and no provider supplies a codec for this state. The bytes are the
/// payload of the reserved persistence owner below, so the existing coordinator rules (matching
/// vanilla operation, generation identity, protected corrupt/unsupported data) apply unchanged.
/// </summary>
internal static class StoryStateCodec
{
    /// <summary>Reserved owner namespace of API-managed story content.</summary>
    internal const string Owner = "vgmodapi.story-content";
    internal const int SchemaVersion = 1;
    private const uint Magic = 0x31435356; // VSC1, little-endian.
    /// <summary>Envelope payloads are capped at 1 MiB; this stays well below that bound.</summary>
    internal const int MaxBytes = 512 * 1024;

    internal static byte[] Encode(IEnumerable<StoryOccurrenceEntry> entries)
    {
        var rows = (entries ?? throw new ArgumentNullException(nameof(entries))).OrderBy(entry => entry.Sequence).ToArray();
        if (rows.Length > StoryLedger.MaxOccurrences) throw new InvalidDataException("Too many story occurrences to persist.");
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(SchemaVersion);
            writer.Write(rows.Length);
            foreach (var row in rows)
            {
                WriteSegment(writer, row.Id.Provider);
                WriteSegment(writer, row.Id.LocalId);
                writer.Write(row.OccurrenceId.ToByteArray());
                writer.Write(row.Sequence);
                writer.Write((byte)row.State);
                writer.Write((byte)(row.Outcome.HasValue ? (int)row.Outcome.Value + 1 : 0));
                writer.Write((byte)row.Retention);
                var choices = row.Retention == StoryRetention.Campaign ? row.Choices : new Dictionary<string, string>(StringComparer.Ordinal);
                if (choices.Count > StoryLedger.MaxChoices) throw new InvalidDataException("Too many declared choices to persist.");
                writer.Write((byte)choices.Count);
                foreach (var pair in choices.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteText(writer, pair.Key, StoryLedger.MaxChoiceKeyLength);
                    WriteText(writer, pair.Value, StoryLedger.MaxChoiceValueLength);
                }
            }
            writer.Flush();
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Story state exceeds its bounded payload size.");
        return bytes;
    }

    internal static StoryOccurrenceEntry[] Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid story state size.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unsupported story state.");
        int version = reader.ReadInt32();
        // A newer payload is never downgraded; the coordinator reports it as unsupported and protects it.
        if (version != SchemaVersion) throw new InvalidDataException("Unsupported story state version " + version + ".");
        int count = reader.ReadInt32();
        if (count < 0 || count > StoryLedger.MaxOccurrences) throw new InvalidDataException("Malformed story state count.");
        var rows = new StoryOccurrenceEntry[count];
        for (int index = 0; index < count; index++)
        {
            var provider = ReadSegment(reader);
            var local = ReadSegment(reader);
            var occurrence = new Guid(ReadExact(reader, 16));
            long sequence = reader.ReadInt64();
            var state = (StoryOccurrenceState)reader.ReadByte();
            int outcomeCode = reader.ReadByte();
            var retention = (StoryRetention)reader.ReadByte();
            if (!Enum.IsDefined(typeof(StoryOccurrenceState), state) || !Enum.IsDefined(typeof(StoryRetention), retention))
                throw new InvalidDataException("Malformed story occurrence state.");
            StoryOutcome? outcome = null;
            if (outcomeCode != 0)
            {
                var value = (StoryOutcome)(outcomeCode - 1);
                if (!Enum.IsDefined(typeof(StoryOutcome), value)) throw new InvalidDataException("Malformed story outcome.");
                outcome = value;
            }
            if ((state == StoryOccurrenceState.Retired) != outcome.HasValue)
                throw new InvalidDataException("A retired occurrence requires exactly one outcome.");
            int choiceCount = reader.ReadByte();
            if (choiceCount > StoryLedger.MaxChoices) throw new InvalidDataException("Malformed story choice count.");
            if (choiceCount > 0 && retention != StoryRetention.Campaign) throw new InvalidDataException("Temporary retention carries no declared choices.");
            var choices = new List<KeyValuePair<string, string>>(choiceCount);
            for (int choice = 0; choice < choiceCount; choice++)
            {
                var key = ReadText(reader, StoryLedger.MaxChoiceKeyLength);
                var value = ReadText(reader, StoryLedger.MaxChoiceValueLength);
                if (key.Length == 0) throw new InvalidDataException("Malformed story choice key.");
                choices.Add(new KeyValuePair<string, string>(key, value));
            }
            if (choices.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                throw new InvalidDataException("Duplicate story choice key.");
            rows[index] = new StoryOccurrenceEntry(new StoryContentId(provider, local), occurrence, retention, sequence, state, outcome, choices);
        }
        if (stream.Position != bytes.Length) throw new InvalidDataException("Trailing story state bytes.");
        if (rows.Select(row => row.OccurrenceId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("Duplicate story occurrence identity.");
        return rows;
    }

    /// <summary>Structural validation used as the persistence provider's validate callback.</summary>
    internal static bool Validate(byte[] bytes)
    {
        try { Decode(bytes); return true; }
        catch (InvalidDataException) { return false; }
        catch (ArgumentException) { return false; }
        catch (EndOfStreamException) { return false; }
    }

    /// <summary>A short read is truncation, not a smaller record: BinaryReader would otherwise return fewer bytes silently.</summary>
    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new InvalidDataException("Truncated story state.");
        return bytes;
    }

    private static void WriteSegment(BinaryWriter writer, string value)
    {
        if (!StoryContentId.IsValidSegment(value)) throw new InvalidDataException("Invalid story identity segment.");
        var bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((byte)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadSegment(BinaryReader reader)
    {
        int length = reader.ReadByte();
        if (length is < 1 or > 48) throw new InvalidDataException("Invalid story identity length.");
        var value = Encoding.ASCII.GetString(ReadExact(reader, length));
        if (!StoryContentId.IsValidSegment(value)) throw new InvalidDataException("Invalid story identity segment.");
        return value;
    }

    private static void WriteText(BinaryWriter writer, string value, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (value != null && value.Length > max) throw new InvalidDataException("Story text exceeds its bound.");
        if (bytes.Length > max * 4) throw new InvalidDataException("Story text exceeds its encoded bound.");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int max)
    {
        int length = reader.ReadUInt16();
        if (length > max * 4) throw new InvalidDataException("Story text exceeds its encoded bound.");
        var value = Encoding.UTF8.GetString(ReadExact(reader, length));
        if (value.Length > max) throw new InvalidDataException("Story text exceeds its bound.");
        return value;
    }
}

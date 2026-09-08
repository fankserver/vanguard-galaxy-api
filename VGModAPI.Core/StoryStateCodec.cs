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
    /// <summary>
    /// Schema 2 adds the choices declared for an outcome the game has not produced yet, and the fact
    /// that the game reported a mission failed while still holding it. Schema 1 is read unchanged:
    /// its rows simply have neither, which is exactly what they meant.
    /// </summary>
    internal const int SchemaVersion = 4;
    internal const int FirstSchemaVersion = 1;
    private const uint Magic = 0x31435356; // VSC1, little-endian.
    /// <summary>Strict UTF-8 on BOTH sides: invalid bytes or unpaired surrogates throw instead of decoding to U+FFFD.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    /// <summary>Envelope payloads are capped at 1 MiB; this stays well below that bound.</summary>
    internal const int MaxBytes = 512 * 1024;
    /// <summary>Magic, schema version and row count.</summary>
    internal const int HeaderBytes = 12;

    /// <summary>Whether text can be written at all under strict UTF-8; unpaired surrogates cannot.</summary>
    internal static bool IsEncodable(string value)
    {
        try { StrictUtf8.GetByteCount(value ?? ""); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    /// <summary>Encoded size of text in the bytes this codec writes.</summary>
    internal static int Utf8Bytes(string value) => StrictUtf8.GetByteCount(value ?? "");

    /// <summary>
    /// Exact encoded cost of one occurrence, so the ledger can enforce the payload bound in the same
    /// units the codec writes. Encoding is deterministic, so this is a size, not an estimate.
    /// </summary>
    private static readonly Dictionary<string, string> Empty = new(StringComparer.Ordinal);

    /// <summary>Encoded cost of the choices staged for an outcome the game has not produced yet.</summary>
    internal static int PendingSize(StoryOccurrenceEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (entry.State == StoryOccurrenceState.Retired) return 0;
        int size = 0;
        foreach (var pair in entry.PendingChoices)
            try { size += 2 + StrictUtf8.GetByteCount(pair.Key) + 2 + StrictUtf8.GetByteCount(pair.Value); }
            catch (EncoderFallbackException) { throw new InvalidDataException("Invalid UTF-8 in story text."); }
        return size;
    }

    internal static int EncodedSize(StoryOccurrenceEntry entry)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        // The row carries a pending-choice block and a flags byte as well since schema 2.
        int size = 1 + entry.Id.Provider!.Length + 1 + entry.Id.LocalId.Length + 16 + 8 + 3 + 2 + 1 + 1 + 1 + PendingSize(entry) + (entry.ObjectiveLayout.Slots.Count == 0 ? 0 : StoryObjectiveLayoutCodec.EncodedSize(entry.ObjectiveLayout));
        if (entry.RetainedDefinition != null) size += 4 + StoryDefinitionCodec.Encode(entry.RetainedDefinition).Length;
        if (entry.Retention != StoryRetention.Campaign) return size;
        foreach (var pair in entry.Choices)
            // Strict UTF-8 again: text that cannot be encoded has no size, it is simply refused.
            try { size += 2 + StrictUtf8.GetByteCount(pair.Key) + 2 + StrictUtf8.GetByteCount(pair.Value); }
            catch (EncoderFallbackException) { throw new InvalidDataException("Invalid UTF-8 in story text."); }
        return size;
    }

    internal static byte[] Encode(IEnumerable<StoryOccurrenceEntry> entries)
    {
        var rows = (entries ?? throw new ArgumentNullException(nameof(entries))).OrderBy(entry => entry.Sequence).ToArray();
        // Encoder and decoder enforce the SAME ledger bounds; neither is more permissive.
        var refusal = StoryLedger.RefuseBounds(rows);
        if (refusal != null) throw new InvalidDataException(refusal);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, true))
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
                writer.Write((ushort)row.ChoiceReservation);
                var choices = row.Retention == StoryRetention.Campaign ? row.Choices : new Dictionary<string, string>(StringComparer.Ordinal);
                if (choices.Count > StoryMissionDefinition.MaxChoiceKeys) throw new InvalidDataException("Too many declared choices to persist.");
                writer.Write((byte)choices.Count);
                foreach (var pair in choices.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteText(writer, pair.Key, StoryMissionDefinition.MaxChoiceKeyBytes);
                    WriteText(writer, pair.Value, StoryMissionDefinition.MaxChoiceValueBytes);
                }
                var pending = row.State == StoryOccurrenceState.Retired ? Empty : row.PendingChoices;
                if (pending.Count > StoryMissionDefinition.MaxChoiceKeys) throw new InvalidDataException("Too many declared choices to persist.");
                writer.Write((byte)pending.Count);
                foreach (var pair in pending.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    WriteText(writer, pair.Key, StoryMissionDefinition.MaxChoiceKeyBytes);
                    WriteText(writer, pair.Value, StoryMissionDefinition.MaxChoiceValueBytes);
                }
                bool hasLayout = row.ObjectiveLayout.Slots.Count != 0;
                writer.Write((byte)((row.FailureObserved ? 1 : 0) | (hasLayout ? 2 : 0) | (row.RetainedDefinition != null ? 4 : 0)));
                if (hasLayout) StoryObjectiveLayoutCodec.Write(writer, row.ObjectiveLayout);
                if (row.RetainedDefinition != null)
                {
                    var definition = StoryDefinitionCodec.Encode(row.RetainedDefinition);
                    writer.Write(definition.Length);
                    writer.Write(definition);
                }
            }
            writer.Flush();
        }
        return stream.ToArray();
    }

    internal static StoryOccurrenceEntry[] Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 12 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid story state size.");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, StrictUtf8, true);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unsupported story state.");
        int version = reader.ReadInt32();
        // A newer payload is never downgraded; the coordinator reports it as unsupported and protects
        // it. An OLDER one is read as what it meant: schema 1 rows carry no pending declaration and no
        // observed failure, so those are simply absent rather than guessed.
        if (version < FirstSchemaVersion || version > SchemaVersion)
            throw new InvalidDataException("Unsupported story state version " + version + ".");
        int count = reader.ReadInt32();
        if (count < 0 || count > StoryLedger.MaxOccurrences) throw new InvalidDataException("Malformed story state count.");
        var rows = new StoryOccurrenceEntry[count];
        for (int index = 0; index < count; index++)
        {
            var provider = ReadSegment(reader);
            var local = ReadSegment(reader);
            var occurrence = new Guid(ReadExact(reader, 16));
            long sequence = reader.ReadInt64();
            if (sequence < 1) throw new InvalidDataException("Malformed story occurrence sequence.");
            if (index > 0 && sequence <= rows[index - 1].Sequence)
                throw new InvalidDataException("Story occurrence sequences must be strictly increasing.");
            var state = (StoryOccurrenceState)reader.ReadByte();
            int outcomeCode = reader.ReadByte();
            var retention = (StoryRetention)reader.ReadByte();
            int reservation = reader.ReadUInt16();
            if (reservation > StoryMissionDefinition.MaxChoiceBytesPerOccurrence)
                throw new InvalidDataException("Malformed story choice reservation.");
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
            if (choiceCount > StoryMissionDefinition.MaxChoiceKeys) throw new InvalidDataException("Malformed story choice count.");
            if (choiceCount > 0 && retention != StoryRetention.Campaign) throw new InvalidDataException("Temporary retention carries no declared choices.");
            var choices = new List<KeyValuePair<string, string>>(choiceCount);
            for (int choice = 0; choice < choiceCount; choice++)
            {
                var key = ReadText(reader, StoryMissionDefinition.MaxChoiceKeyBytes);
                var value = ReadText(reader, StoryMissionDefinition.MaxChoiceValueBytes);
                if (key.Length == 0) throw new InvalidDataException("Malformed story choice key.");
                choices.Add(new KeyValuePair<string, string>(key, value));
            }
            if (choices.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != choices.Count)
                throw new InvalidDataException("Duplicate story choice key.");
            var pending = new List<KeyValuePair<string, string>>();
            bool failure = false;
            bool hasLayout = false;
            bool hasDefinition = false;
            if (version >= 2)
            {
                int pendingCount = reader.ReadByte();
                if (pendingCount > StoryMissionDefinition.MaxChoiceKeys) throw new InvalidDataException("Malformed story choice count.");
                for (int choice = 0; choice < pendingCount; choice++)
                {
                    var key = ReadText(reader, StoryMissionDefinition.MaxChoiceKeyBytes);
                    var value = ReadText(reader, StoryMissionDefinition.MaxChoiceValueBytes);
                    if (key.Length == 0) throw new InvalidDataException("Malformed story choice key.");
                    pending.Add(new KeyValuePair<string, string>(key, value));
                }
                if (pending.Select(pair => pair.Key).Distinct(StringComparer.Ordinal).Count() != pending.Count)
                    throw new InvalidDataException("Duplicate story choice key.");
                int flags = reader.ReadByte();
                if (flags > (version >= 4 ? 7 : version >= 3 ? 3 : 1)) throw new InvalidDataException("Malformed story occurrence flags.");
                failure = (flags & 1) != 0;
                hasLayout = (flags & 2) != 0;
                hasDefinition = (flags & 4) != 0;
            }
            var layout = hasLayout ? StoryObjectiveLayoutCodec.Read(reader) : null;
            if (hasLayout && layout!.Slots.Count == 0) throw new InvalidDataException("Empty objective layout must omit its presence flag.");
            StoryMissionDefinition? definition = null;
            if (hasDefinition)
            {
                int length = reader.ReadInt32();
                if (length < 1 || length > StoryDefinitionCodec.MaxBytes || length > stream.Length - stream.Position)
                    throw new InvalidDataException("Invalid retained definition length.");
                definition = StoryDefinitionCodec.Decode(reader.ReadBytes(length));
            }
            rows[index] = new StoryOccurrenceEntry(new StoryContentId(provider, local), occurrence, retention, sequence, state, outcome, choices, reservation, pending, failure, layout, definition);
        }
        if (stream.Position != bytes.Length) throw new InvalidDataException("Trailing story state bytes.");
        var refusal = StoryLedger.RefuseBounds(rows);
        if (refusal != null) throw new InvalidDataException(refusal);
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

    /// <summary>Text bounds are ENCODED bytes on both sides, so a byte budget means one thing everywhere.</summary>
    private static void WriteText(BinaryWriter writer, string value, int maxBytes)
    {
        byte[] bytes;
        // Strict UTF-8: an unpaired surrogate is refused here rather than written as a replacement character.
        try { bytes = StrictUtf8.GetBytes(value ?? ""); }
        catch (EncoderFallbackException) { throw new InvalidDataException("Invalid UTF-8 in story text."); }
        if (bytes.Length > maxBytes) throw new InvalidDataException("Story text exceeds its encoded bound.");
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int maxBytes)
    {
        int length = reader.ReadUInt16();
        if (length > maxBytes) throw new InvalidDataException("Story text exceeds its encoded bound.");
        try { return StrictUtf8.GetString(ReadExact(reader, length)); }
        catch (DecoderFallbackException) { throw new InvalidDataException("Invalid UTF-8 in story state."); }
    }
}

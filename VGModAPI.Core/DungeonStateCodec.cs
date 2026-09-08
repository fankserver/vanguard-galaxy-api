using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal sealed class DungeonOccurrence
{
    internal readonly Guid Id;
    internal readonly DungeonDefinitionId DefinitionId;
    internal readonly DungeonDefinition Definition;
    internal readonly Dictionary<string, string> Choices;
    internal DungeonOccurrence(Guid id, DungeonDefinitionId definitionId, DungeonDefinition definition, IEnumerable<KeyValuePair<string, string>>? choices = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Occurrence identity required.");
        Id = id; DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId)); Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Choices = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in choices ?? Array.Empty<KeyValuePair<string, string>>())
        {
            var item = definition.Events.SingleOrDefault(e => e.Id == pair.Key);
            if (item == null || !item.Choices.Any(c => c.Id == pair.Value)) throw new ArgumentException("Unknown persisted event or choice.");
            Choices.Add(pair.Key, pair.Value);
        }
    }
}

/// <summary>API-owned selected choices and retained definition snapshots. No executable callbacks or game objects.</summary>
internal static class DungeonStateCodec
{
    internal const int MaximumOccurrences = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(IEnumerable<DungeonOccurrence> occurrences)
    {
        var entries = occurrences.ToArray();
        if (entries.Length > MaximumOccurrences || entries.Select(e => e.Id).Distinct().Count() != entries.Length) throw new InvalidDataException("Invalid occurrence collection.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(1); writer.Write(entries.Length);
        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            writer.Write(entry.Id.ToByteArray()); Text(writer, entry.DefinitionId.ProviderId); Text(writer, entry.DefinitionId.LocalId);
            var definition = DungeonDefinitionCodec.Encode(entry.Definition); writer.Write(definition.Length); writer.Write(definition);
            writer.Write(entry.Choices.Count);
            foreach (var pair in entry.Choices.OrderBy(p => p.Key, StringComparer.Ordinal)) { Text(writer, pair.Key); Text(writer, pair.Value); }
            if (stream.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Dungeon state exceeds provider payload limit.");
        }
        return stream.ToArray();
    }
    internal static IReadOnlyList<DungeonOccurrence> Decode(byte[] payload)
    {
        if (payload == null || payload.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Invalid dungeon state size.");
        using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream, Utf8, true);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported dungeon state schema.");
        var count = Count(reader, MaximumOccurrences); var entries = new List<DungeonOccurrence>(); var ids = new HashSet<Guid>();
        for (var index = 0; index < count; index++)
        {
            var guid = reader.ReadBytes(16); if (guid.Length != 16) throw new EndOfStreamException();
            var id = new Guid(guid); if (id == Guid.Empty || !ids.Add(id)) throw new InvalidDataException("Duplicate or empty dungeon occurrence identity.");
            var definitionId = new DungeonDefinitionId(Text(reader), Text(reader));
            var length = Count(reader, DungeonDefinitionCodec.MaximumBytes);
            if (length > stream.Length - stream.Position) throw new EndOfStreamException();
            var definition = DungeonDefinitionCodec.Decode(reader.ReadBytes(length));
            var choices = new Dictionary<string, string>(StringComparer.Ordinal); var choiceCount = Count(reader, 128);
            for (var c = 0; c < choiceCount; c++) choices.Add(Text(reader), Text(reader));
            entries.Add(new DungeonOccurrence(id, definitionId, definition, choices));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing dungeon state bytes.");
        return entries.AsReadOnly();
    }
    internal static bool Validate(byte[] payload)
    {
        try { Decode(payload); return true; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static int Count(BinaryReader reader, int maximum)
    { var value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidDataException("Invalid dungeon state count."); return value; }
    private static void CheckId(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || text.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))) throw new InvalidDataException("Invalid dungeon state identifier.");
    }
    private static void Text(BinaryWriter writer, string text)
    { CheckId(text); var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Text(BinaryReader reader)
    {
        var length = Count(reader, 512); if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
        var value = Utf8.GetString(reader.ReadBytes(length)); CheckId(value); return value;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

internal sealed class WorldSavedDefinition
{
    internal string Owner { get; }
    internal WorldCombatDefinition Definition { get; }
    internal WorldSavedDefinition(string owner, WorldCombatDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _ = new ContentDeclaration(owner, definition.LocalId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        Owner = owner;
    }
}

/// <summary>Retained declaration data, not mutable native POI state or provider code.</summary>
internal static class WorldDefinitionCodec
{
    internal const string Owner = "vgmodapi.world-definitions";
    internal const int SchemaVersion = 1;
    private const int Magic = 0x31444756;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(WorldSavedDefinition[] definitions)
    {
        if (definitions == null || definitions.Length > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid retained definition count.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write(SchemaVersion); writer.Write(definitions.Length);
        var keys = new HashSet<(string, string)>();
        foreach (var saved in definitions)
        {
            if (saved == null || !keys.Add((saved.Owner, saved.Definition.LocalId))) throw new InvalidDataException("Duplicate retained world definition.");
            var d = saved.Definition;
            Write(writer, saved.Owner); Write(writer, d.LocalId); writer.Write(d.Revision);
            Write(writer, d.Name); Write(writer, d.FactionId); writer.Write(d.Level);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Retained definitions exceed quota.");
        }
        writer.Flush(); return stream.ToArray();
    }
    internal static WorldSavedDefinition[] Decode(byte[] payload)
    {
        if (payload == null || payload.Length < 12 || payload.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Missing or oversized retained definitions.");
        using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream, Utf8, true);
        try
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion) throw new InvalidDataException("Unsupported retained definition format.");
            int count = reader.ReadInt32();
            if (count < 0 || count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid retained definition count.");
            var result = new WorldSavedDefinition[count]; var keys = new HashSet<(string, string)>();
            for (int i = 0; i < count; i++)
            {
                string owner = Read(reader, 128), local = Read(reader, 128); int revision = reader.ReadInt32();
                var definition = new WorldCombatDefinition(local, revision, Read(reader, 1024), Read(reader, 128), reader.ReadInt32());
                if (!keys.Add((owner, local))) throw new InvalidDataException("Duplicate retained world definition.");
                result[i] = new WorldSavedDefinition(owner, definition);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing retained definition data.");
            return result;
        }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Malformed retained world definitions.", error); }
    }
    private static void Write(BinaryWriter writer, string text)
    { var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
    private static string Read(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > limit) throw new InvalidDataException("Invalid retained text length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated retained text.");
        return Utf8.GetString(bytes);
    }
}

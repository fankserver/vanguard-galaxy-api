using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

/// <summary>
/// One persisted authored-pocket occurrence row. Retained declarative content (occurrence key, revision,
/// declared gate state) plus the fresh per-game native references owned by the occurrence. Nothing
/// executable or callback-shaped is stored; every live reference is re-resolved per session.
/// </summary>
internal sealed class AuthoredSystemOccurrence
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string OccurrenceKey { get; }
    internal int Revision { get; }
    internal string SystemId { get; }
    internal string EntranceGateId { get; }
    internal string PocketGateId { get; }
    internal bool DeclaredOpen { get; set; }
    internal AuthoredSystemOccurrence(string owner, string localId, string occurrenceKey, int revision,
        string systemId, string entranceGateId, string pocketGateId, bool declaredOpen)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(occurrenceKey) || WorldStateCodec.TextByteCount(occurrenceKey) > 256) throw new ArgumentException("A bounded occurrence key is required.", nameof(occurrenceKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) throw new ArgumentException("A bounded native system identity is required.", nameof(systemId));
        if (string.IsNullOrWhiteSpace(entranceGateId) || WorldStateCodec.TextByteCount(entranceGateId) > 128) throw new ArgumentException("A bounded native gate identity is required.", nameof(entranceGateId));
        if (string.IsNullOrWhiteSpace(pocketGateId) || WorldStateCodec.TextByteCount(pocketGateId) > 128) throw new ArgumentException("A bounded native gate identity is required.", nameof(pocketGateId));
        Owner = owner; LocalId = localId; OccurrenceKey = occurrenceKey; Revision = revision;
        SystemId = systemId; EntranceGateId = entranceGateId; PocketGateId = pocketGateId; DeclaredOpen = declaredOpen;
    }
}

/// <summary>Bounded owned-authored-system inventory. A new row KIND alongside the WorldSavedObject framing, encoded with the same conventions.</summary>
internal static class AuthoredSystemStateCodec
{
    internal const string Owner = "vgmodapi.world-authored-systems";
    internal const int SchemaVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int Magic = 0x32534756;

    internal static byte[] Encode(IReadOnlyList<AuthoredSystemOccurrence> rows)
    {
        if (rows == null || rows.Count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid authored-system inventory count.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write(SchemaVersion); writer.Write(rows.Count);
        var keys = new HashSet<(string, string, string)>();
        foreach (var row in rows)
        {
            if (row == null || !keys.Add((row.Owner, row.LocalId, row.OccurrenceKey)))
                throw new InvalidDataException("Duplicate authored-system occurrence.");
            WriteText(writer, row.Owner); WriteText(writer, row.LocalId); WriteText(writer, row.OccurrenceKey);
            writer.Write(row.Revision); WriteText(writer, row.SystemId); WriteText(writer, row.EntranceGateId);
            WriteText(writer, row.PocketGateId); writer.Write(row.DeclaredOpen);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("Authored-system metadata exceeds its quota.");
        }
        writer.Flush(); return stream.ToArray();
    }

    internal static AuthoredSystemOccurrence[] Decode(byte[] payload)
    {
        if (payload == null || payload.Length < 12 || payload.Length > WorldSerializationAssociation.MaxMetadataBytes)
            throw new InvalidDataException("Missing or oversized authored-system metadata.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Utf8, true);
        try
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion) throw new InvalidDataException("Unsupported authored-system metadata format.");
            int count = reader.ReadInt32();
            if (count < 0 || count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid authored-system inventory count.");
            var rows = new AuthoredSystemOccurrence[count];
            var keys = new HashSet<(string, string, string)>();
            for (int i = 0; i < count; i++)
            {
                string owner = ReadText(reader, 128), local = ReadText(reader, 128), key = ReadText(reader, 256);
                int revision = reader.ReadInt32();
                var row = new AuthoredSystemOccurrence(owner, local, key, revision,
                    ReadText(reader, 128), ReadText(reader, 128), ReadText(reader, 128), reader.ReadBoolean());
                if (!keys.Add((owner, local, key))) throw new InvalidDataException("Duplicate authored-system occurrence.");
                rows[i] = row;
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing authored-system metadata.");
            return rows;
        }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Malformed authored-system metadata.", error); }
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > limit) throw new InvalidDataException("Invalid authored-system text length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated authored-system text.");
        return Utf8.GetString(bytes);
    }
}

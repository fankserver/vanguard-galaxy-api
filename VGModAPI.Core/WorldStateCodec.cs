using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Ownership metadata for a vanilla-compatible Combat node; native mutable state remains bound by its digest.</summary>
internal sealed class WorldSavedObject
{
    internal WorldObjectIdentity Identity { get; }
    internal string SystemId { get; }
    internal string NativeDigest { get; }
    internal int DefinitionRevision { get; }
    internal WorldSavedObject(WorldObjectIdentity identity, string systemId, string nativeDigest, int definitionRevision)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128)
            throw new ArgumentException("A bounded parent system identity is required.", nameof(systemId));
        if (nativeDigest == null || nativeDigest.Length != 64) throw new ArgumentException("A native SHA-256 digest is required.", nameof(nativeDigest));
        foreach (char c in nativeDigest)
            if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new ArgumentException("Invalid native digest.", nameof(nativeDigest));
        if (definitionRevision < 1) throw new ArgumentOutOfRangeException(nameof(definitionRevision));
        SystemId = systemId; NativeDigest = nativeDigest; DefinitionRevision = definitionRevision;
    }
}

/// <summary>Strict bounded ownership inventory. Decoding is not native admission or proof of a committed generation.</summary>
internal static class WorldStateCodec
{
    internal const string Owner = "vgmodapi.world";
    internal const int SchemaVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static int TextByteCount(string value) => Utf8.GetByteCount(value);
    private const int Magic = 0x31574756;

    internal static byte[] Encode(WorldSavedObject[] rows)
    {
        if (rows == null || rows.Length > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid world inventory count.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(Magic); writer.Write(SchemaVersion); writer.Write(rows.Length);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row == null || !ids.Add(row.Identity.NativeId)) throw new InvalidDataException("Duplicate or null world inventory row.");
            WriteText(writer, row.Identity.Owner); WriteText(writer, row.Identity.LocalId);
            writer.Write(row.Identity.InstanceId.ToByteArray());
            WriteText(writer, row.SystemId); WriteText(writer, row.NativeDigest);
            writer.Write(row.DefinitionRevision);
            if (stream.Length > WorldSerializationAssociation.MaxMetadataBytes) throw new InvalidDataException("World metadata exceeds its quota.");
        }
        writer.Flush(); return stream.ToArray();
    }

    internal static WorldSavedObject[] Decode(byte[] payload)
    {
        if (payload == null || payload.Length < 12 || payload.Length > WorldSerializationAssociation.MaxMetadataBytes)
            throw new InvalidDataException("Missing or oversized world metadata.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Utf8, true);
        try
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != SchemaVersion) throw new InvalidDataException("Unsupported world metadata format.");
            int count = reader.ReadInt32();
            if (count < 0 || count > WorldSerializationAssociation.MaxObjects) throw new InvalidDataException("Invalid world inventory count.");
            var rows = new WorldSavedObject[count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string owner = ReadText(reader, 128), local = ReadText(reader, 128);
                var instance = reader.ReadBytes(16);
                if (instance.Length != 16) throw new InvalidDataException("Truncated world instance identity.");
                var identity = new WorldObjectIdentity(new ContentDeclaration(owner, local, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), new Guid(instance));
                var row = new WorldSavedObject(identity, ReadText(reader, 128), ReadText(reader, 64), reader.ReadInt32());
                if (!ids.Add(identity.NativeId)) throw new InvalidDataException("Duplicate world instance identity.");
                rows[i] = row;
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing world metadata.");
            return rows;
        }
        catch (Exception error) when (error is ArgumentException || error is EndOfStreamException)
        { throw new InvalidDataException("Malformed world metadata.", error); }
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string ReadText(BinaryReader reader, int limit)
    {
        int length = reader.ReadInt32();
        if (length < 1 || length > limit) throw new InvalidDataException("Invalid world text length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("Truncated world text.");
        return Utf8.GetString(bytes);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

internal enum SchemaReadStatus { Ready, Missing, Corrupt, Unsupported, MigrationFailed }

internal sealed class SchemaReadResult
{
    private readonly byte[]? _payload;
    internal SchemaReadStatus Status { get; }
    internal bool Migrated { get; }
    internal byte[]? Payload => _payload == null ? null : (byte[])_payload.Clone();
    internal SchemaReadResult(SchemaReadStatus status, byte[]? payload = null, bool migrated = false)
    { Status = status; _payload = payload == null ? null : (byte[])payload.Clone(); Migrated = migrated; }
}

// Providers own payload formats. This bounded binary envelope introduces no JSON runtime dependency.
internal sealed class OwnerSchemaCodec
{
    internal const int MaxPayload = 1024 * 1024;
    private const int MaxEnvelope = MaxPayload + 128;
    private readonly string _owner;
    private readonly int _version;
    private readonly Func<byte[], bool> _validate;
    private readonly Dictionary<int, Func<byte[], byte[]>> _migrations;

    internal OwnerSchemaCodec(string owner, int version, Func<byte[], bool> validate,
        IReadOnlyDictionary<int, Func<byte[], byte[]>>? migrations = null)
    {
        if (!ValidOwner(owner)) throw new ArgumentException("Canonical owner namespace required.", nameof(owner));
        if (version < 1) throw new ArgumentOutOfRangeException(nameof(version));
        _owner = owner; _version = version; _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        _migrations = new Dictionary<int, Func<byte[], byte[]>>();
        if (migrations != null)
            foreach (var pair in migrations)
            {
                if (pair.Key < 1 || pair.Key >= version || pair.Value == null) throw new ArgumentException("Invalid migration registration.");
                _migrations.Add(pair.Key, pair.Value);
            }
    }

    private static bool ValidOwner(string owner)
    {
        if (owner == null || owner.Length < 1 || owner.Length > 64 || owner[0] < 'a' || owner[0] > 'z') return false;
        foreach (var c in owner) if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '.' && c != '-') return false;
        return true;
    }

    internal byte[] Encode(byte[] payload)
    {
        if (payload == null || payload.Length > MaxPayload) throw new ArgumentException("Payload exceeds bounds.", nameof(payload));
        var owned = (byte[])payload.Clone();
        if (!_validate((byte[])owned.Clone())) throw new InvalidDataException("Provider payload validation failed.");
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, true))
        {
            writer.Write(new byte[] { 86, 71, 79, 83 }); // VGOS
            writer.Write((byte)1);
            writer.Write((byte)_owner.Length);
            writer.Write(Encoding.ASCII.GetBytes(_owner));
            writer.Write(_version);
            writer.Write(owned.Length);
            writer.Write(owned);
        }
        using var sha = SHA256.Create();
        var bytes = body.ToArray();
        var hash = sha.ComputeHash(bytes);
        var result = new byte[bytes.Length + hash.Length];
        Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        Buffer.BlockCopy(hash, 0, result, bytes.Length, hash.Length);
        return result;
    }

    internal SchemaReadResult Decode(byte[]? envelope)
    {
        if (envelope == null) return new SchemaReadResult(SchemaReadStatus.Missing);
        if (envelope.Length < 47 || envelope.Length > MaxEnvelope) return new SchemaReadResult(SchemaReadStatus.Corrupt);
        var bytes = (byte[])envelope.Clone();
        if (bytes[0] != 86 || bytes[1] != 71 || bytes[2] != 79 || bytes[3] != 83) return new SchemaReadResult(SchemaReadStatus.Corrupt);
        if (bytes[4] > 1) return new SchemaReadResult(SchemaReadStatus.Unsupported);
        if (bytes[4] != 1) return new SchemaReadResult(SchemaReadStatus.Corrupt);
        int version;
        byte[] payload;
        try
        {
            int bodyLength = bytes.Length - 32;
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes, 0, bodyLength);
            for (int i = 0; i < hash.Length; i++) if (hash[i] != bytes[bodyLength + i]) return new SchemaReadResult(SchemaReadStatus.Corrupt);
            using var reader = new BinaryReader(new MemoryStream(bytes, 0, bodyLength, false), Encoding.UTF8);
            reader.ReadBytes(5);
            var ownerLength = reader.ReadByte();
            if (ownerLength < 1 || ownerLength > 64) return new SchemaReadResult(SchemaReadStatus.Corrupt);
            var ownerBytes = reader.ReadBytes(ownerLength);
            foreach (var value in ownerBytes) if (value > 127) return new SchemaReadResult(SchemaReadStatus.Corrupt);
            if (Encoding.ASCII.GetString(ownerBytes) != _owner) return new SchemaReadResult(SchemaReadStatus.Corrupt);
            version = reader.ReadInt32();
            int length = reader.ReadInt32();
            if (version < 1 || length < 0 || length > MaxPayload || reader.BaseStream.Position + length != bodyLength)
                return new SchemaReadResult(SchemaReadStatus.Corrupt);
            payload = reader.ReadBytes(length);
        }
        catch (Exception) { return new SchemaReadResult(SchemaReadStatus.Corrupt); }
        if (version > _version) return new SchemaReadResult(SchemaReadStatus.Unsupported);
        bool migrated = version != _version;
        try
        {
            int steps = 0;
            while (version < _version)
            {
                if (++steps > 64 || !_migrations.TryGetValue(version, out var migrate)) return new SchemaReadResult(SchemaReadStatus.MigrationFailed);
                var next = migrate((byte[])payload.Clone());
                if (next == null || next.Length > MaxPayload) return new SchemaReadResult(SchemaReadStatus.MigrationFailed);
                payload = (byte[])next.Clone();
                version++;
            }
            if (!_validate((byte[])payload.Clone())) return new SchemaReadResult(migrated ? SchemaReadStatus.MigrationFailed : SchemaReadStatus.Corrupt);
            return new SchemaReadResult(SchemaReadStatus.Ready, payload, migrated);
        }
        catch (Exception) { return new SchemaReadResult(migrated ? SchemaReadStatus.MigrationFailed : SchemaReadStatus.Corrupt); }
    }
}

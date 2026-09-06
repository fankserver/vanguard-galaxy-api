using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal sealed class MissionIdentityRecord
{
    internal string Fingerprint { get; }
    internal Guid InstanceId { get; }
    internal MissionIdentityRecord(string fingerprint, Guid instanceId)
    {
        if (!ValidFingerprint(fingerprint) || instanceId == Guid.Empty) throw new ArgumentException("Invalid mission identity record.");
        Fingerprint = fingerprint; InstanceId = instanceId;
    }
    internal static bool ValidFingerprint(string? value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>Snapshot correspondence only: callers must associate these records with the exact serialized vanilla snapshot.</summary>
internal static class MissionIdentitySnapshot
{
    internal const int MaxEntries = 4096, MaxBytes = 8 + MaxEntries * 48;
    private const uint Magic = 0x31494d56; // VMI1, little-endian.
    internal static byte[] Encode(IEnumerable<MissionIdentityRecord> records)
    {
        var rows = records.Take(MaxEntries + 1).ToArray(); CheckRows(rows);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write(rows.Length);
        foreach (var row in rows.OrderBy(r => r.Fingerprint, StringComparer.Ordinal).ThenBy(r => r.InstanceId))
        {
            for (int i = 0; i < 64; i += 2) writer.Write(Convert.ToByte(row.Fingerprint.Substring(i, 2), 16));
            writer.Write(row.InstanceId.ToByteArray());
        }
        writer.Flush(); return stream.ToArray();
    }
    internal static MissionIdentityRecord[] Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 8 || bytes.Length > MaxBytes) throw new InvalidDataException("Invalid mission identity snapshot size.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Unsupported mission identity snapshot.");
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxEntries || bytes.Length != 8 + count * 48) throw new InvalidDataException("Malformed mission identity snapshot.");
        var rows = new MissionIdentityRecord[count];
        for (int i = 0; i < count; i++)
        {
            var fingerprint = BitConverter.ToString(reader.ReadBytes(32)).Replace("-", "").ToLowerInvariant();
            rows[i] = new MissionIdentityRecord(fingerprint, new Guid(reader.ReadBytes(16)));
        }
        CheckRows(rows); return rows;
    }
    private static void CheckRows(IReadOnlyCollection<MissionIdentityRecord> rows)
    {
        if (rows.Count > MaxEntries || rows.Any(r => r == null) || rows.Select(r => r.InstanceId).Distinct().Count() != rows.Count)
            throw new InvalidDataException("Invalid or repeated mission occurrence identity.");
    }
    internal static Guid?[] MatchUnique(IReadOnlyCollection<MissionIdentityRecord> saved, IReadOnlyList<string> currentFingerprints)
    {
        CheckRows(saved);
        if (currentFingerprints.Count > MaxEntries || currentFingerprints.Any(f => !MissionIdentityRecord.ValidFingerprint(f))) throw new InvalidDataException("Invalid current mission fingerprints.");
        var prior = saved.GroupBy(r => r.Fingerprint, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var current = currentFingerprints.GroupBy(f => f, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return currentFingerprints.Select(f => prior.TryGetValue(f, out var matches) && matches.Length == 1 && current[f] == 1 ? (Guid?)matches[0].InstanceId : null).ToArray();
    }
}

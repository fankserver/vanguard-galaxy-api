using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal static class DungeonPodResumeCodec
{
    internal const int MaximumPods = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(IEnumerable<DungeonPodResumeState> pods)
    {
        var entries = pods.ToArray();
        if (entries.Length > MaximumPods || entries.Any(p => p == null) || entries.Select(p => p.Id).Distinct().Count() != entries.Length)
            throw new InvalidDataException("Invalid saved pod collection.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(1); writer.Write(entries.Length);
        foreach (var pod in entries.OrderBy(p => p.Id))
        {
            writer.Write(pod.Id.ToByteArray()); writer.Write(pod.OperationId.ToByteArray());
            var parent = Utf8.GetBytes(pod.ParentShipId); writer.Write(parent.Length); writer.Write(parent);
            writer.Write((byte)pod.Phase);
            writer.Write((byte)((pod.PlayerOwned ? 1 : 0) | (pod.ReturnManifestKnown ? 2 : 0) | (pod.ReturnDelivered ? 4 : 0) | (pod.ReturnAttempted ? 8 : 0)));
            writer.Write(pod.ReturnCrew.Count);
            foreach (var pair in pod.ReturnCrew.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var bytes = Utf8.GetBytes(pair.Key); writer.Write(bytes.Length); writer.Write(bytes); writer.Write(pair.Value);
            }
            writer.Write((byte)(pod.Transport == null ? 0 : 1));
            if (pod.Transport is { } transport)
            {
                var nativeId = Utf8.GetBytes(transport.NativePodId); writer.Write(nativeId.Length); writer.Write(nativeId);
                var donor = Utf8.GetBytes(transport.DonorShipId); writer.Write(donor.Length); writer.Write(donor);
                writer.Write((byte)(transport.PendingReinforcement ? 1 : 0));
                foreach (var value in transport.Pose) writer.Write(value);
                writer.Write(transport.OutboundCrew.Count);
                foreach (var pair in transport.OutboundCrew.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                { var key = Utf8.GetBytes(pair.Key); writer.Write(key.Length); writer.Write(key); writer.Write(pair.Value); }
            }
            if (stream.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Pod recovery payload exceeds save limit.");
        }
        return stream.ToArray();
    }
    internal static IReadOnlyList<DungeonPodResumeState> Decode(byte[] payload)
    {
        if (payload == null || payload.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Invalid pod recovery payload size.");
        using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream, Utf8, true);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported pod recovery schema.");
        var count = Count(reader, MaximumPods); var entries = new List<DungeonPodResumeState>(); var ids = new HashSet<Guid>();
        for (var index = 0; index < count; index++)
        {
            var id = Id(reader); var occurrence = Id(reader);
            var parentLength = Count(reader, 512); if (parentLength > stream.Length - stream.Position) throw new EndOfStreamException();
            var parent = Utf8.GetString(reader.ReadBytes(parentLength));
            var phase = (DungeonPodPhase)reader.ReadByte(); var flags = reader.ReadByte();
            if ((flags & ~15) != 0 || !ids.Add(id)) throw new InvalidDataException("Invalid or duplicate pod metadata.");
            var crew = new Dictionary<string, int>(StringComparer.Ordinal); var crewCount = Count(reader, 64);
            for (var c = 0; c < crewCount; c++)
            {
                var length = Count(reader, 512); if (length > stream.Length - stream.Position) throw new EndOfStreamException();
                crew.Add(Utf8.GetString(reader.ReadBytes(length)), reader.ReadInt32());
            }
            DungeonPodTransport? transport = null;
            var present = reader.ReadByte(); if (present > 1) throw new InvalidDataException("Invalid transport flag.");
            if (present == 1)
            {
                var nativeId = Text(reader); var donor = Text(reader); var reinforcement = reader.ReadByte(); if (reinforcement > 1) throw new InvalidDataException("Invalid reinforcement flag.");
                var pose = new float[9]; for (var p = 0; p < pose.Length; p++) pose[p] = reader.ReadSingle();
                var outbound = new Dictionary<string, int>(StringComparer.Ordinal); var outboundCount = Count(reader, 64);
                for (var c = 0; c < outboundCount; c++) outbound.Add(Text(reader), reader.ReadInt32());
                transport = new(nativeId, reinforcement == 1, outbound, pose, donor);
            }
            entries.Add(new(id, occurrence, phase, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, crew, (flags & 8) != 0, parent, transport));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing pod recovery data.");
        return entries.AsReadOnly();
    }
    internal static bool Validate(byte[] payload)
    {
        try { Decode(payload); return true; }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { return false; }
    }
    private static string Text(BinaryReader reader)
    {
        var length = Count(reader, 512); if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
        return Utf8.GetString(reader.ReadBytes(length));
    }
    private static Guid Id(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException();
        var id = new Guid(bytes); if (id == Guid.Empty) throw new InvalidDataException("Missing pod identity."); return id;
    }
    private static int Count(BinaryReader reader, int max)
    { var value = reader.ReadInt32(); if (value < 0 || value > max) throw new InvalidDataException("Pod data count exceeds bounds."); return value; }
}

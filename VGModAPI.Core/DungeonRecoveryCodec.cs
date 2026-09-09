using System;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

internal static class DungeonRecoveryCodec
{
    internal static byte[] Encode(byte[] operations, byte[] pods)
    {
        Check(operations, pods);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(operations.Length); writer.Write(operations); writer.Write(pods.Length); writer.Write(pods);
        if (stream.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Recovery envelope exceeds save limit.");
        return stream.ToArray();
    }
    internal static (byte[] Operations, byte[] Pods) Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Invalid recovery envelope.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported recovery schema.");
        var operations = Part(reader); var pods = Part(reader);
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing recovery data.");
        Check(operations, pods); return (operations, pods);
    }
    internal static bool Validate(byte[] bytes)
    {
        try { Decode(bytes); return true; }
        catch (Exception error) when (error is IOException or ArgumentException) { return false; }
    }
    private static byte[] Part(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid recovery section length.");
        return reader.ReadBytes(length);
    }
    private static void Check(byte[] operations, byte[] pods)
    {
        var ids = DungeonOperationResumeCodec.Decode(operations).Select(state => state.Id).ToHashSet();
        if (DungeonPodResumeCodec.Decode(pods).Any(pod => !ids.Contains(pod.OperationId))) throw new InvalidDataException("Pod references an absent operation.");
    }
}

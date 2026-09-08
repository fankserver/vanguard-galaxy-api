using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal static class DungeonOperationResumeCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(IEnumerable<DungeonOperationResumeState> states)
    {
        var entries = states.Take(257).ToArray();
        if (entries.Length > 256 || entries.Select(state => state.Id).Distinct().Count() != entries.Length) throw new InvalidDataException("Invalid operation collection.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(4); writer.Write(entries.Length);
        foreach (var entry in entries.OrderBy(state => state.Id))
        {
            writer.Write(entry.Id.ToByteArray()); writer.Write(entry.LocationId.ToByteArray()); writer.Write((entry.ContentOccurrence ?? Guid.Empty).ToByteArray());
            foreach (var text in new[] { entry.AttackerShipId, entry.DungeonType, entry.NativePhase, entry.Outcome, entry.MissionProtection })
            { var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); }
            writer.Write((byte)entry.TerminalProgress); writer.Write((byte)(entry.Autonomous ? 1 : 0));
            DungeonOperationOptionsCodec.Write(writer, entry.Options);
            DungeonDonorApproachCodec.Write(writer, entry.Donors);
            writer.Write((byte)(entry.WalkDispatched ? 1 : 0));
        }
        if (stream.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Operation state exceeds bound.");
        return stream.ToArray();
    }
    internal static IReadOnlyList<DungeonOperationResumeState> Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Invalid operation payload.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 4) throw new InvalidDataException("Unsupported operation schema.");
        var count = reader.ReadInt32(); if (count < 0 || count > 256) throw new InvalidDataException("Invalid operation count.");
        var entries = new List<DungeonOperationResumeState>(); var ids = new HashSet<Guid>();
        for (var i = 0; i < count; i++)
        {
            var id = Id(reader); var location = Id(reader); var content = Id(reader); var text = new string[5];
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate operation identity.");
            for (var t = 0; t < text.Length; t++)
            {
                var length = reader.ReadInt32(); if (length < 0 || length > 512 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid operation field length.");
                text[t] = Utf8.GetString(reader.ReadBytes(length));
            }
            var terminal = (DungeonTerminalProgress)reader.ReadByte(); var autonomous = reader.ReadByte(); if (autonomous > 1) throw new InvalidDataException("Invalid operation ownership flag.");
            var options = DungeonOperationOptionsCodec.Read(reader); var donors = DungeonDonorApproachCodec.Read(reader);
            var walk = reader.ReadByte(); if (walk > 1) throw new InvalidDataException("Invalid walk dispatch state.");
            entries.Add(new(id, location, content == Guid.Empty ? null : content, text[0], text[1], text[2], text[3], text[4], terminal, autonomous == 1, options, donors, walk == 1));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing operation data.");
        return entries.AsReadOnly();
    }
    private static Guid Id(BinaryReader reader)
    { var bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); return new Guid(bytes); }
}

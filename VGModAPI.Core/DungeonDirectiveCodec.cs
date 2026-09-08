using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal static class DungeonDirectiveCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(IEnumerable<DungeonDirectiveState> directives)
    {
        var list = directives.Take(4097).ToArray(); if (list.Length > 4096) throw new InvalidDataException("Too many pending directives.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(list.Length);
        foreach (var entry in list)
        {
            writer.Write(entry.Target); writer.Write(entry.Priority); writer.Write(entry.Filter); writer.Write(entry.ClaimingSlot);
            var crew = entry.RequiredCrew == null ? null : Utf8.GetBytes(entry.RequiredCrew);
            writer.Write(crew?.Length ?? -1); if (crew != null) writer.Write(crew);
        }
        if (stream.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Directive save exceeds payload bound.");
        return stream.ToArray();
    }
    internal static IReadOnlyList<DungeonDirectiveState> Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length > OwnerSchemaCodec.MaxPayload) throw new InvalidDataException("Invalid directive payload size.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported directive schema.");
        var count = reader.ReadInt32(); if (count < 0 || count > 4096) throw new InvalidDataException("Invalid directive count.");
        var list = new List<DungeonDirectiveState>();
        for (var i = 0; i < count; i++)
        {
            var target = reader.ReadInt32(); var priority = reader.ReadInt32(); var filter = reader.ReadInt32(); var slot = reader.ReadInt32(); var length = reader.ReadInt32();
            if (length < -1 || length > 512 || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid required crew length.");
            list.Add(new(target, priority, length == -1 ? null : Utf8.GetString(reader.ReadBytes(length)), filter, slot));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing directive data.");
        return list.AsReadOnly();
    }
}

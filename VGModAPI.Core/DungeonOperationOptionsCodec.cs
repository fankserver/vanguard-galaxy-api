using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal static class DungeonOperationOptionsCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static void Write(BinaryWriter writer, DungeonOperationOptions? options)
    {
        writer.Write((byte)(options == null ? 0 : 1)); if (options == null) return;
        Text(writer, options.Ammo); Text(writer, options.Stealth);
        writer.Write((byte)((options.AutoMove ? 1 : 0) | (options.AutoAcceptBuyOut ? 2 : 0)));
        writer.Write(options.PriorityCompartment ?? -1); writer.Write(options.AssignedCrew.Count);
        foreach (var pair in options.AssignedCrew.OrderBy(pair => pair.Key, System.StringComparer.Ordinal)) { Text(writer, pair.Key); writer.Write(pair.Value); }
    }
    internal static DungeonOperationOptions? Read(BinaryReader reader)
    {
        var present = reader.ReadByte(); if (present == 0) return null; if (present != 1) throw new InvalidDataException("Invalid options marker.");
        var ammo = Text(reader); var stealth = Text(reader); var flags = reader.ReadByte(); var priority = reader.ReadInt32(); var count = reader.ReadInt32();
        if (flags > 3 || priority < -1 || count < 0 || count > 64) throw new InvalidDataException("Invalid saved options.");
        var crew = new Dictionary<string, int>(System.StringComparer.Ordinal);
        for (var i = 0; i < count; i++) crew.Add(Text(reader), reader.ReadInt32());
        return new(crew, ammo, stealth, (flags & 2) != 0, (flags & 1) != 0, priority == -1 ? null : priority);
    }
    internal static void Text(BinaryWriter writer, string value)
    { var bytes = Utf8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); }
    internal static string Text(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > 512 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid option text length.");
        return Utf8.GetString(reader.ReadBytes(length));
    }
}

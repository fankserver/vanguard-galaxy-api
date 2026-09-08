using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

internal static class DungeonWalkReturnCodec
{
    internal static void Write(BinaryWriter writer, DungeonWalkReturnState? state)
    {
        writer.Write((byte)(state == null ? 0 : (int)state.Progress + 1)); if (state == null) return;
        writer.Write(state.Crew.Count);
        foreach (var pair in state.Crew.OrderBy(pair => pair.Key, System.StringComparer.Ordinal))
        { DungeonOperationOptionsCodec.Text(writer, pair.Key); writer.Write(pair.Value); }
    }
    internal static DungeonWalkReturnState? Read(BinaryReader reader)
    {
        var marker = reader.ReadByte(); if (marker == 0) return null;
        if (marker > 3) throw new InvalidDataException("Invalid walk return progress.");
        var count = reader.ReadInt32(); if (count < 0 || count > 64) throw new InvalidDataException("Invalid walk return crew count.");
        var crew = new Dictionary<string, int>(System.StringComparer.Ordinal);
        for (var i = 0; i < count; i++) crew.Add(DungeonOperationOptionsCodec.Text(reader), reader.ReadInt32());
        return new(crew, (DungeonWalkReturnProgress)(marker - 1));
    }
}

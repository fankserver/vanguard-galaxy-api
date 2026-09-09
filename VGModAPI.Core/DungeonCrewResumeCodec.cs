using System.IO;

namespace VGModAPI.Core;

internal static class DungeonCrewResumeCodec
{
    internal static byte[] Encode(DungeonCrewResumeState state)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)1); writer.Write(state.DirectiveTarget); writer.Write(state.FleeDelay);
        writer.Write((byte)(state.Withdrawing ? 1 : 0)); writer.Write(state.RetreatOrigin);
        writer.Write(state.RecoveryProgress); writer.Write(state.DazedTime);
        return stream.ToArray();
    }
    internal static DungeonCrewResumeState Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length != 22) throw new InvalidDataException("Invalid crew execution payload length.");
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported crew execution schema.");
        var directive = reader.ReadInt32(); var flee = reader.ReadSingle(); var withdrawing = reader.ReadByte();
        if (withdrawing > 1) throw new InvalidDataException("Invalid withdrawal flag.");
        return new(directive, flee, withdrawing == 1, reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
    }
}

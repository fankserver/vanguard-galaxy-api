using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

internal static class StoryObjectiveLayoutCodec
{
    internal static int EncodedSize(StoryObjectiveLayout layout)
    {
        int size = 6;
        foreach (var slot in layout.Slots) size += 12 + slot.Key.Length;
        return size;
    }

    internal static void Write(BinaryWriter writer, StoryObjectiveLayout layout)
    {
        writer.Write((byte)layout.Slots.Count);
        writer.Write(layout.Revision);
        writer.Write((byte)(layout.FullyScripted ? 1 : 0));
        foreach (var slot in layout.Slots)
        {
            writer.Write((byte)slot.Key.Length);
            writer.Write(Encoding.ASCII.GetBytes(slot.Key));
            writer.Write((byte)slot.Step);
            writer.Write((byte)slot.Objective);
            writer.Write((byte)slot.Kind);
            writer.Write(slot.Required);
            writer.Write(slot.Progress);
        }
    }

    internal static StoryObjectiveLayout Read(BinaryReader reader)
    {
        int count = reader.ReadByte();
        if (count > StoryObjectiveLayout.MaxSlots) throw new InvalidDataException("Objective layout exceeds its bound.");
        int revision = reader.ReadInt32();
        int fullyScripted = reader.ReadByte();
        if (fullyScripted > 1) throw new InvalidDataException("Invalid source-layout flags.");
        var slots = new List<StoryObjectiveLayout.Slot>(count);
        string? previous = null;
        for (int i = 0; i < count; i++)
        {
            int length = reader.ReadByte();
            if (length is < 1 or > 48) throw new InvalidDataException("Invalid objective key length.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            foreach (byte value in bytes) if (value > 127) throw new InvalidDataException("Non-ASCII objective key.");
            var key = Encoding.ASCII.GetString(bytes);
            if (previous != null && string.CompareOrdinal(previous, key) >= 0) throw new InvalidDataException("Objective keys are not canonical.");
            slots.Add(new StoryObjectiveLayout.Slot(key, reader.ReadByte(), reader.ReadByte(), (StoryObjectiveKind)reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32()));
            previous = key;
        }
        try { return new StoryObjectiveLayout(slots, revision, fullyScripted == 1); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid objective layout.", error); }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Bounded retained definitions; native catalogs are validated separately before world mutation.</summary>
internal static class DungeonDefinitionCodec
{
    internal const int MaximumBytes = 128 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] Encode(DungeonDefinition definition)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(1); writer.Write(definition.AllowHazards); writer.Write(definition.AllowScheduledReinforcements); writer.Write(definition.Version); Text(writer, definition.Name); Text(writer, definition.FactionId ?? "");
        writer.Write(definition.Layout.Compartments.Count);
        foreach (var room in definition.Layout.Compartments)
        {
            Text(writer, room.Id); Text(writer, room.Type.ToString()); writer.Write(room.Locked);
            writer.Write(room.Adjacent.Count); foreach (var id in room.Adjacent) Text(writer, id);
            writer.Write(room.Defenders.Count); foreach (var pair in room.Defenders) { Text(writer, pair.Key); writer.Write(pair.Value); }
        }
        writer.Write(definition.Events.Count);
        foreach (var item in definition.Events)
        {
            Text(writer, item.Id); Text(writer, item.CompartmentId); Text(writer, item.Text); writer.Write(item.Choices.Count);
            foreach (var choice in item.Choices)
            {
                Text(writer, choice.Id); Text(writer, choice.Text); Text(writer, choice.RequiredCrewId ?? ""); writer.Write(choice.Loot.Count);
                foreach (var loot in choice.Loot) { Text(writer, loot.ItemId); writer.Write(loot.Amount); }
            }
        }
        writer.Flush(); if (stream.Length > MaximumBytes) throw new InvalidDataException("Dungeon definition exceeds retained-data limit.");
        return stream.ToArray();
    }
    internal static DungeonDefinition Decode(byte[] payload)
    {
        if (payload == null || payload.Length > MaximumBytes) throw new InvalidDataException("Dungeon definition payload exceeds bounds.");
        using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream, Utf8, true);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported dungeon definition encoding.");
        var hazards = reader.ReadByte(); var reinforcements = reader.ReadByte();
        if (hazards > 1 || reinforcements > 1) throw new InvalidDataException("Invalid dungeon rule flags.");
        var version = reader.ReadInt32(); var name = Text(reader, 256); var faction = Text(reader, 128);
        var rooms = new DungeonCompartmentDefinition[Count(reader, 64)];
        for (var i = 0; i < rooms.Length; i++)
        {
            var id = Text(reader, 128); var typeName = Text(reader, 128);
            if (!Enum.TryParse<CompartmentType>(typeName, out var type) || !Enum.IsDefined(typeof(CompartmentType), type) || type.ToString() != typeName) throw new InvalidDataException("Unsupported compartment type.");
            var lockedByte = reader.ReadByte(); if (lockedByte > 1) throw new InvalidDataException("Invalid lock flag.");
            var neighbors = new string[Count(reader, 8)]; for (var n = 0; n < neighbors.Length; n++) neighbors[n] = Text(reader, 128);
            var defenders = new Dictionary<string, int>(StringComparer.Ordinal);
            var count = Count(reader, 32); for (var d = 0; d < count; d++) defenders.Add(Text(reader, 128), reader.ReadInt32());
            rooms[i] = new(id, type, neighbors, lockedByte == 1, defenders);
        }
        var events = new DungeonEventDefinition[Count(reader, 128)];
        for (var i = 0; i < events.Length; i++)
        {
            var id = Text(reader, 128); var compartment = Text(reader, 128); var text = Text(reader, 4000);
            var choices = new DungeonChoiceDefinition[Count(reader, 8)];
            for (var c = 0; c < choices.Length; c++)
            {
                var choiceId = Text(reader, 128); var choiceText = Text(reader, 1000); var specialist = Text(reader, 128);
                var loot = new DungeonLootDefinition[Count(reader, 16)];
                for (var l = 0; l < loot.Length; l++) loot[l] = new(Text(reader, 128), reader.ReadInt32());
                choices[c] = new(choiceId, choiceText, specialist.Length == 0 ? null : specialist, loot);
            }
            events[i] = new(id, compartment, text, choices);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing dungeon definition bytes.");
        return new(version, name, new DungeonLayout(rooms), faction.Length == 0 ? null : faction, events, hazards == 1, reinforcements == 1);
    }
    private static int Count(BinaryReader reader, int maximum)
    {
        var count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("Dungeon collection exceeds bounds."); return count;
    }
    private static void Text(BinaryWriter writer, string text)
    {
        var bytes = Utf8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
        if (writer.BaseStream.Length > MaximumBytes) throw new InvalidDataException("Dungeon definition exceeds retained-data limit.");
    }
    private static string Text(BinaryReader reader, int maximumCharacters)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > maximumCharacters * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid dungeon text length.");
        var text = Utf8.GetString(reader.ReadBytes(length)); if (text.Length > maximumCharacters) throw new InvalidDataException("Dungeon text exceeds bounds."); return text;
    }
}

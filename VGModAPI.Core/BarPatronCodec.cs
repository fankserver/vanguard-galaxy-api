using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal static class BarPatronCodec
{
    internal const string Owner = "vgmodapi.bar-patrons";
    internal const int SchemaVersion = 2;
    internal const int MaxProviders = 32, MaxPerProvider = 32, MaxBytes = 256 * 1024;
    internal const int HeaderBytes = 12, ProviderBytes = (MaxBytes - HeaderBytes) / MaxProviders;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(IEnumerable<BarPatronState> values) => Encode(values, SchemaVersion);

    private static byte[] Encode(IEnumerable<BarPatronState> values, int version)
    {
        if (values == null) throw new ArgumentNullException(nameof(values));
        var rows = values.Take(MaxProviders * MaxPerProvider + 1).ToArray();
        if (rows.Length > MaxProviders * MaxPerProvider || rows.Any(row => row == null)) throw new InvalidDataException("Invalid patron count.");
        if (rows.Select(row => row.Id).Distinct().Count() != rows.Length) throw new InvalidDataException("Duplicate patron identity.");
        var groups = rows.GroupBy(row => row.Id.Provider, StringComparer.Ordinal).ToArray();
        if (groups.Length > MaxProviders || groups.Any(group => group.Count() > MaxPerProvider)) throw new InvalidDataException("Patron provider quota exceeded.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(0x31504256); // VBP1
        writer.Write(version); writer.Write(rows.Length);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows.OrderBy(row => row.Id.Provider, StringComparer.Ordinal).ThenBy(row => row.Id.LocalId, StringComparer.Ordinal))
        {
            long start = stream.Position;
            Write(writer, row.Id.Provider); Write(writer, row.Id.LocalId); Write(writer, row.Station);
            Write(writer, row.Name); Write(writer, row.Description); Write(writer, row.Seed);
            writer.Write((byte)((row.Mission.HasValue ? 1 : 0) | (row.Portrait != null ? 2 : 0) | (!row.IsMale ? 4 : 0)));
            if (row.Mission.HasValue)
            {
                Write(writer, row.Mission.Value.LocalId);
                writer.Write(row.Occurrence!.Value.ToByteArray());
            }
            if (row.Portrait != null)
            {
                writer.Write((byte)(row.Portrait.PortraitName != null ? 0 : 1));
                Write(writer, row.Portrait.PortraitName ?? row.Portrait.RegistryName!);
            }
            sizes.TryGetValue(row.Id.Provider, out var size);
            sizes[row.Id.Provider] = size + stream.Position - start;
            if (sizes[row.Id.Provider] > ProviderBytes || stream.Position > MaxBytes) throw new InvalidDataException("Patron payload quota exceeded.");
        }
        writer.Flush();
        return stream.ToArray();
    }

    internal static BarPatronState[] Decode(byte[] payload)
    {
        if (payload == null || payload.Length < 12 || payload.Length > MaxBytes) throw new InvalidDataException("Invalid patron payload size.");
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, Utf8);
        if (reader.ReadInt32() != 0x31504256) throw new InvalidDataException("Unsupported patron format.");
        var version = reader.ReadInt32();
        if (version is not 1 and not SchemaVersion) throw new InvalidDataException("Unsupported patron format.");
        int count = reader.ReadInt32();
        if (count < 0 || count > MaxProviders * MaxPerProvider) throw new InvalidDataException("Invalid patron count.");
        var rows = new BarPatronState[count];
        for (int index = 0; index < count; index++)
        {
            var id = new BarPatronId(Read(reader, 48), Read(reader, 48));
            var station = Read(reader, 128); var name = Read(reader, 128); var description = Read(reader, 1024); var seed = Read(reader, 128);
            byte flags = reader.ReadByte();
            if (flags > (version == 1 ? 1 : 7)) throw new InvalidDataException("Invalid patron reference flag.");
            StoryContentId? mission = null; Guid? occurrence = null;
            if ((flags & 1) != 0)
            {
                mission = new StoryContentId(id.Provider, Read(reader, 48));
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16) throw new InvalidDataException("Truncated patron occurrence.");
                occurrence = new Guid(bytes);
            }
            CharacterPortrait? portrait = null;
            if ((flags & 2) != 0)
                portrait = reader.ReadByte() switch
                {
                    0 => CharacterPortrait.Named(Read(reader, 256)),
                    1 => CharacterPortrait.OfCharacter(Read(reader, 2048)),
                    _ => throw new InvalidDataException("Unknown patron portrait kind.")
                };
            rows[index] = new BarPatronState(id, station, name, description, seed, mission, occurrence, portrait, isMale: (flags & 4) == 0);
        }
        if (stream.Position != stream.Length || !Encode(rows, version).SequenceEqual(payload)) throw new InvalidDataException("Noncanonical patron payload.");
        return rows;
    }

    internal static bool Validate(byte[] payload)
    {
        try { Decode(payload); return true; }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is ArgumentException || error is OverflowException) { return false; }
    }

    private static void Write(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length)); writer.Write(bytes);
    }
    private static string Read(BinaryReader reader, int max)
    {
        int size = reader.ReadUInt16();
        if (size > max || size > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid patron text length.");
        return Utf8.GetString(reader.ReadBytes(size));
    }
}

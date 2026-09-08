using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

internal static class DungeonDonorApproachCodec
{
    internal static void Write(BinaryWriter writer, IReadOnlyList<DungeonDonorApproachState> donors)
    {
        writer.Write(donors.Count);
        foreach (var donor in donors.OrderBy(item => item.ShipId, System.StringComparer.Ordinal))
        {
            DungeonOperationOptionsCodec.Text(writer, donor.ShipId); writer.Write(donor.Crew.Count);
            foreach (var pair in donor.Crew.OrderBy(item => item.Key, System.StringComparer.Ordinal))
            { DungeonOperationOptionsCodec.Text(writer, pair.Key); writer.Write(pair.Value); }
        }
    }
    internal static IReadOnlyList<DungeonDonorApproachState> Read(BinaryReader reader)
    {
        var count = reader.ReadInt32(); if (count < 0 || count > 64) throw new InvalidDataException("Invalid donor count.");
        var donors = new List<DungeonDonorApproachState>(); var ids = new HashSet<string>(System.StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var id = DungeonOperationOptionsCodec.Text(reader); if (!ids.Add(id)) throw new InvalidDataException("Duplicate donor identity.");
            var crewCount = reader.ReadInt32(); if (crewCount < 1 || crewCount > 64) throw new InvalidDataException("Invalid donor crew count.");
            var crew = new Dictionary<string, int>(System.StringComparer.Ordinal);
            for (var j = 0; j < crewCount; j++) crew.Add(DungeonOperationOptionsCodec.Text(reader), reader.ReadInt32());
            donors.Add(new(id, crew));
        }
        return donors.AsReadOnly();
    }
}

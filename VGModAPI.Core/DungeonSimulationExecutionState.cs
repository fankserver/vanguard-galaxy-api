using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonSimulationExecutionState
{
    internal float ExplosionTimer { get; }
    internal IReadOnlyList<int> VentTargets { get; }
    internal DungeonSimulationExecutionState(float timer, IEnumerable<int> targets)
    {
        var items = targets.Take(4097).ToArray();
        if (float.IsNaN(timer) || float.IsInfinity(timer) || timer < 0 || items.Length > 4096 || items.Any(value => value < 0 || value >= 4096) || items.Distinct().Count() != items.Length)
            throw new InvalidDataException("Invalid simulation execution state.");
        ExplosionTimer = timer; VentTargets = Array.AsReadOnly(items.OrderBy(value => value).ToArray());
    }
    internal void ValidateRooms(int count)
    { if (VentTargets.Any(index => index >= count)) throw new InvalidDataException("Structural vent refers to an absent compartment."); }
    internal byte[] Encode()
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(1); writer.Write(ExplosionTimer); writer.Write(VentTargets.Count);
        foreach (var index in VentTargets) writer.Write(index);
        return stream.ToArray();
    }
    internal static DungeonSimulationExecutionState Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported simulation execution schema.");
        var timer = reader.ReadSingle(); var count = reader.ReadInt32();
        if (count < 0 || count > 4096 || stream.Length - stream.Position != count * 4L) throw new InvalidDataException("Invalid structural vent payload.");
        var targets = new int[count]; for (var i = 0; i < count; i++) targets[i] = reader.ReadInt32();
        return new(timer, targets);
    }
}

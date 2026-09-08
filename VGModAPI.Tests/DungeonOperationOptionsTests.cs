using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonOperationOptionsTests
{
    [Fact]
    public void OptionsCodecRejectsEveryTruncatedPayloadAndInvalidFlags()
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        DungeonOperationOptionsCodec.Write(writer, new(new Dictionary<string, int> { ["Marine"] = 1 }, "a", "s", false, false, null));
        var bytes = stream.ToArray();
        for (var length = 0; length < bytes.Length; length++)
        {
            using var input = new System.IO.MemoryStream(bytes, 0, length, false); using var reader = new System.IO.BinaryReader(input);
            Assert.ThrowsAny<Exception>(() => DungeonOperationOptionsCodec.Read(reader));
        }
        bytes[11] = 4;
        using var corrupt = new System.IO.BinaryReader(new System.IO.MemoryStream(bytes));
        Assert.Throws<System.IO.InvalidDataException>(() => DungeonOperationOptionsCodec.Read(corrupt));
    }
    [Fact]
    public void DispatchedWalkApproachCannotBeTreatedAsPreDispatchAfterRoundTrip()
    {
        var saved = new DungeonOperationResumeState(Guid.NewGuid(), Guid.NewGuid(), null, "ship", "Station", "Approach", "", "", DungeonTerminalProgress.NotStarted, false, walkDispatched: true);
        var restored = Assert.Single(DungeonOperationResumeCodec.Decode(DungeonOperationResumeCodec.Encode(new[] { saved })));
        Assert.Equal("Approach", restored.NativePhase);
        Assert.True(restored.WalkDispatched);
    }
    [Fact]
    public void AssignedCrewBoundsAndCopiesAreEnforced()
    {
        Assert.Throws<ArgumentException>(() => new DungeonOperationOptions(new Dictionary<string, int> { ["A"] = 10000, ["B"] = 1 }, "a", "s", false, false, null));
        Assert.Throws<ArgumentException>(() => new DungeonOperationOptions(new Dictionary<string, int>(), "a", "s", false, false, -1));
    }
    [Fact]
    public void PreSimulationOptionsRoundTripWithoutCurrentPreferenceDefaultsOrAliasing()
    {
        var adapter = new DungeonOperationOptionsAdapter(new DungeonLayoutBuilderTests.Native(), () => new NativeObject(), (_, value) => value);
        var crew = new Dictionary<string, int> { ["Marine"] = 3 };
        var resolved = new DungeonOperationOptions(crew, "ammo", "stealth", true, true, 2);
        var source = adapter.Restore(resolved); crew["Marine"] = 9;
        var state = new DungeonOperationResumeState(Guid.NewGuid(), Guid.NewGuid(), null, "ship", "HostileShip", "Approach", "", "mission", DungeonTerminalProgress.NotStarted, false, adapter.Capture(source));
        var restored = DungeonOperationResumeCodec.Decode(DungeonOperationResumeCodec.Encode(new[] { state }))[0];
        var options = adapter.Capture(adapter.Restore(restored.Options!));
        Assert.Equal(3, options.AssignedCrew["Marine"]); Assert.Equal("ammo", options.Ammo); Assert.Equal("stealth", options.Stealth);
        Assert.True(options.AutoMove); Assert.True(options.AutoAcceptBuyOut); Assert.Equal(2, options.PriorityCompartment);
    }
}

using System;
using System.Collections.Generic;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;
namespace VGModAPI.Tests;
[Collection("Dungeon crew resume")]
public sealed class DungeonSimulationExecutionTests
{
    private static NativeObject Simulation(int rooms)
    {
        var simulation = new NativeObject(); simulation.Fields["compartments"] = new object[rooms];
        simulation.Fields["friendlyUnits"] = Array.Empty<object>(); simulation.Fields["hostileUnits"] = Array.Empty<object>();
        simulation.Fields["resumeExplosionTimer"] = 0f; simulation.Fields["resumeVentTargets"] = new HashSet<int>(); return simulation;
    }
    [Fact]
    public void PatchRoundTripRetainsExplosionElapsedTimeAndAlreadyVentedRooms()
    {
        var native = new DungeonLayoutBuilderTests.Native();
        DungeonCrewResumePatches.Coordinator = new(native, new DungeonCrewResumeJson(typeof(JsonValue).Assembly), _ => throw new InvalidOperationException("Unexpected rejection"), execution: true);
        try
        {
            var source = Simulation(4); source.Fields["resumeExplosionTimer"] = 4.5f; source.Fields["resumeVentTargets"] = new HashSet<int> { 1, 3 };
            var json = new JsonValue(new JsonObject()); DungeonCrewResumePatches.SimulationSave.Postfix(source, json);
            var restored = Simulation(4); DungeonCrewResumePatches.SimulationLoad.Postfix(restored, json);
            Assert.True(DungeonCrewResumePatches.Tick.Prefix(restored));
            Assert.Equal(4.5f, restored.Fields["resumeExplosionTimer"]);
            var vents = Assert.IsType<HashSet<int>>(restored.Fields["resumeVentTargets"]); Assert.True(vents.SetEquals(new[] { 1, 3 }));
            Assert.False(vents.Add(1));
            restored.Fields["resumeExplosionTimer"] = (float)restored.Fields["resumeExplosionTimer"]! + 0.5f;
            Assert.Equal(5f, restored.Fields["resumeExplosionTimer"]);
            var again = new JsonValue(new JsonObject()); DungeonCrewResumePatches.SimulationSave.Postfix(restored, again);
            var next = Simulation(4); DungeonCrewResumePatches.SimulationLoad.Postfix(next, again);
            Assert.Equal(5f, next.Fields["resumeExplosionTimer"]);
        }
        finally { DungeonCrewResumePatches.Coordinator = null; }
    }
    [Fact]
    public void VentToMissingCompartmentQuarantinesRestoreBeforeTickOrSave()
    {
        var native = new DungeonLayoutBuilderTests.Native(); var faults = 0;
        DungeonCrewResumePatches.Coordinator = new(native, new DungeonCrewResumeJson(typeof(JsonValue).Assembly), _ => faults++, execution: true);
        try
        {
            var json = new JsonValue(new JsonObject());
            new DungeonCrewResumeJson(typeof(JsonValue).Assembly).WriteExecution(json, new(1.5f, new[] { 3 }));
            var restored = Simulation(2); DungeonCrewResumePatches.SimulationLoad.Postfix(restored, json);
            Assert.False(DungeonCrewResumePatches.Tick.Prefix(restored)); Assert.Equal(1, faults);
            Assert.Throws<InvalidOperationException>(() => DungeonCrewResumePatches.SimulationSave.Prefix(restored));
        }
        finally { DungeonCrewResumePatches.Coordinator = null; }
    }
    [Fact]
    public void InvalidTimerAndDuplicateHistoryAreRejected()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => new DungeonSimulationExecutionState(float.NaN, Array.Empty<int>()));
        Assert.Throws<System.IO.InvalidDataException>(() => new DungeonSimulationExecutionState(-1f, Array.Empty<int>()));
        Assert.Throws<System.IO.InvalidDataException>(() => new DungeonSimulationExecutionState(1f, new[] { 1, 1 }));
    }
}

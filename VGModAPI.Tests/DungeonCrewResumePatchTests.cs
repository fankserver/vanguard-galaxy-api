using System;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Patches;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

[CollectionDefinition("Dungeon crew resume", DisableParallelization = true)]
public sealed class DungeonCrewResumeCollection { }

[Collection("Dungeon crew resume")]
public sealed class DungeonCrewResumePatchTests
{
    [Fact]
    public void SaveRestoreAndSecondSavePreserveExecutionAndCorruptionBlocksTickAndSave()
    {
        var native = new DungeonLayoutBuilderTests.Native(); var adapter = new DungeonCrewResumeAdapter(native);
        var faults = 0;
        DungeonCrewResumePatches.Coordinator = new(native, new DungeonCrewResumeJson(typeof(JsonValue).Assembly), _ => faults++);
        try
        {
            var crew = new NativeObject(); var saved = new DungeonCrewResumeState(1, 1.5f, true, 0, 0.2f, 4);
            adapter.Restore(crew, saved, 2);
            var json = new JsonValue(new JsonObject()); DungeonCrewResumePatches.Save.Postfix(crew, json);
            var restored = new NativeObject(); restored.Fields["hp"] = 11;
            DungeonCrewResumePatches.Load.Postfix(json, restored);
            var simulation = new NativeObject(); simulation.Fields["compartments"] = new object[2];
            simulation.Fields["friendlyUnits"] = new[] { restored }; simulation.Fields["hostileUnits"] = Array.Empty<object>();
            DungeonCrewResumePatches.SimulationLoad.Postfix(simulation);
            Assert.True(DungeonCrewResumePatches.Tick.Prefix(simulation)); Assert.Equal(11, restored.Fields["hp"]);
            var second = new JsonValue(new JsonObject()); DungeonCrewResumePatches.SimulationSave.Prefix(simulation);
            DungeonCrewResumePatches.Save.Postfix(restored, second);
            Assert.Equal(json.AsJsonObject["vgmodapiCrewExecution"].AsString, second.AsJsonObject["vgmodapiCrewExecution"].AsString);
            var bad = new JsonObject(); bad["vgmodapiCrewExecution"] = "corrupt";
            var broken = new NativeObject(); DungeonCrewResumePatches.Load.Postfix(new JsonValue(bad), broken);
            var brokenSim = new NativeObject(); brokenSim.Fields["compartments"] = new object[2]; brokenSim.Fields["friendlyUnits"] = new[] { broken }; brokenSim.Fields["hostileUnits"] = Array.Empty<object>();
            DungeonCrewResumePatches.SimulationLoad.Postfix(brokenSim);
            Assert.False(DungeonCrewResumePatches.Tick.Prefix(brokenSim)); Assert.Throws<InvalidOperationException>(() => DungeonCrewResumePatches.SimulationSave.Prefix(brokenSim));
            var operation = new NativeObject(); operation.Fields["simulation"] = brokenSim;
            var director = 0; var reinforcement = 0; var terminal = 0;
            void OuterTick()
            {
                if (!DungeonOperationMutationGate.Allows(operation, native, DungeonCrewResumePatches.Coordinator!.CanTick)) return;
                director++;
                if (DungeonCrewResumePatches.Tick.Prefix(operation.Fields["simulation"]!)) { }
                reinforcement++; terminal++;
            }
            OuterTick(); Assert.Equal(0, director); Assert.Equal(0, reinforcement); Assert.Equal(0, terminal);
            operation.Fields["simulation"] = simulation; OuterTick();
            Assert.Equal(1, director); Assert.Equal(1, reinforcement); Assert.Equal(1, terminal);
            Assert.True(DungeonCrewResumePatches.Tick.Prefix(simulation)); Assert.Equal(2, faults);
        }
        finally { DungeonCrewResumePatches.Coordinator = null; }
    }
}

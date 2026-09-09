using System;
using System.Collections;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonDirectiveResumeTests
{
    [Fact]
    public void SimulationRoundTripRebindsClaimToRestoredUnitAndPreservesSecondSave()
    {
        var native = new DungeonLayoutBuilderTests.Native(); var json = new DungeonCrewResumeJson(typeof(JsonValue).Assembly);
        var directives = new DungeonDirectiveAdapter(native, () => new NativeObject(), (_, value) => value);
        var faults = 0; var coordinator = new DungeonCrewResumeCoordinator(native, json, _ => faults++, directives);
        var crew = new NativeObject(); new DungeonCrewResumeAdapter(native).Restore(crew, new(1, 1, true, 0, 0.2f, 3), 2);
        var list = new ArrayList(); directives.Restore(list, new[] { new DungeonDirectiveState(1, 3, "Marine", 0, 0) }, new[] { crew }, 2);
        var simulation = Simulation(crew, list); var crewJson = new JsonValue(new JsonObject()); var simJson = new JsonValue(new JsonObject());
        coordinator.SaveCrew(crew, crewJson); coordinator.SaveSimulation(simulation, simJson);
        var restoredCrew = new NativeObject(); coordinator.LoadCrew(restoredCrew, crewJson);
        var restoredList = new ArrayList(); var restoredSim = Simulation(restoredCrew, restoredList);
        Assert.True(coordinator.LoadSimulation(restoredSim, simJson));
        var restoredDirective = Assert.IsType<NativeObject>(Assert.Single(restoredList));
        Assert.Same(restoredCrew, restoredDirective.Fields["directiveUnit"]); Assert.NotSame(crew, restoredDirective.Fields["directiveUnit"]);
        var second = new JsonValue(new JsonObject()); coordinator.SaveSimulation(restoredSim, second);
        Assert.Equal(simJson.AsJsonObject["vgmodapiDirectives"].AsString, second.AsJsonObject["vgmodapiDirectives"].AsString); Assert.Equal(0, faults);
        restoredCrew.Fields["resumeDirectiveTarget"] = 0;
        Assert.Throws<InvalidOperationException>(() => directives.Restore(restoredList, new[] { new DungeonDirectiveState(1, 3, null, 0, 0) }, new[] { restoredCrew }, 2));
        Assert.Same(restoredDirective, Assert.Single(restoredList));
    }
    private static NativeObject Simulation(object crew, ArrayList directives)
    {
        var result = new NativeObject(); result.Fields["compartments"] = new object[2]; result.Fields["friendlyUnits"] = new[] { crew };
        result.Fields["hostileUnits"] = Array.Empty<object>(); result.Fields["pendingDirectives"] = directives; return result;
    }
}

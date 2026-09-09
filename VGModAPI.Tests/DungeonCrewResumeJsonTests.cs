using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DungeonCrewResumeJsonTests
{
    [Fact]
    public void SupplementRoundTripsBesideNativeStateAndRejectsCorruptPresentData()
    {
        var bridge = new DungeonCrewResumeJson(typeof(JsonValue).Assembly);
        var json = new JsonObject(); json["crewTypeId"] = "Marine"; var value = new JsonValue(json);
        Assert.Null(bridge.Read(value));
        var state = new DungeonCrewResumeState(1, 1.2f, true, 0, 0.3f, 5);
        bridge.Write(value, state);
        Assert.Equal("Marine", json["crewTypeId"].AsString);
        Assert.Equal(DungeonCrewResumeCodec.Encode(state), DungeonCrewResumeCodec.Encode(bridge.Read(value)!));
        json["vgmodapiCrewExecution"] = new JsonValue(123);
        Assert.Throws<InvalidDataException>(() => bridge.Read(value));
        json["vgmodapiCrewExecution"] = "invalid";
        Assert.Throws<InvalidDataException>(() => bridge.Read(value));
    }
}

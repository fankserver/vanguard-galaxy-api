using System.Collections.Generic;

namespace Source.MissionSystem
{
    public class Mission
    {
        public LightJson.JsonValue ToJson() => new(new LightJson.JsonObject { Text = name });
        public string name = "Test mission";
        public string? storyId;
        public bool failed;
        public List<MissionStep> steps { get; } = new();
    }
    public class MissionStep { public List<object> objectives { get; } = new(); }
}
namespace Source.MissionSystem.Objectives
{
    public enum ItemCategory { Ore, Salvage }
    public class Mining { public ItemCategory? itemCategory; }
    public class Salvage : Mining { }
}

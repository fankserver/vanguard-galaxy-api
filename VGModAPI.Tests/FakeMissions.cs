using System.Collections.Generic;
using System.Linq;

namespace Source.MissionSystem
{
    public class Mission
    {
        public LightJson.JsonValue ToJson() => new(new LightJson.JsonObject { Text = name });
        /// <summary>
        /// The game's own save shape, reproduced where it MATTERS: sourceFaction.identifier is read
        /// unconditionally, so a mission without a source faction throws while the player saves.
        /// </summary>
        public string SerializeLikeTheGame()
            => "{name:" + name + ",story:" + storyId + ",faction:" + sourceFaction!.identifier
               + ",poi:" + (sourcePoi?.guid ?? "null") + ",difficulty:" + difficulty
               + ",dynamicLevel:" + dynamicLevel
               + ",steps:[" + string.Join(",", steps.Select(step => step.SerializeLikeTheGame()))
               + "],rewards:[" + string.Join(",", rewards.Select(reward => reward.ToJson())) + "]}";
        public string name = "Test mission";
        public string? description, category, completionText, sourceName, iconName;
        public MissionDifficulty difficulty;
        public Source.Galaxy.Faction? sourceFaction;
        public Source.Galaxy.MapPointOfInterest? sourcePoi;
        public Source.Galaxy.MapPointOfInterest? turnIn;
        public bool canAbandon, dynamicLevel, trackedOnHud;
        public string? storyId;
        public string? nextMissionOnFailed;
        public bool failed;
        public List<MissionStep> steps { get; } = new();
        public List<MissionReward> rewards { get; } = new();
    }
    public class MissionStep
    {
        public List<object> objectives { get; } = new();
        public string? description;
        public bool requireAllObjectives;
        public bool hidden;
        public string SerializeLikeTheGame()
            => "{" + description + ":" + requireAllObjectives + ":["
               + string.Join(",", objectives.Select(objective => ((MissionObjective)objective).ToJson())) + "]}";
    }
}
namespace Source.MissionSystem.Objectives
{
    public enum ItemCategory { Ore, Salvage }
    public class Mining { public ItemCategory? itemCategory; }
    public class Salvage : Mining { }
}

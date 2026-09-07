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
        /// <summary>
        /// A HOST PROJECTION of the mission's persisted shape, not the game's own serializer running:
        /// it reads the same fields the game writes, including the ones a guard could plausibly
        /// disturb, so a test can show that nothing about the object changed. Native serialization
        /// evidence remains PR3 work.
        /// </summary>
        public string SerializeLikeTheGame()
            => "{name:" + name + ",story:" + storyId + ",faction:" + sourceFaction!.identifier
               + ",poi:" + (sourcePoi?.guid ?? "null") + ",turnIn:" + (turnIn?.guid ?? "null")
               + ",description:" + description + ",category:" + category + ",completion:" + completionText
               + ",sourceName:" + sourceName + ",icon:" + iconName
               + ",difficulty:" + difficulty + ",dynamicLevel:" + dynamicLevel
               + ",failed:" + failed + ",canAbandon:" + canAbandon + ",tracked:" + trackedOnHud
               + ",canBeIdled:" + canBeIdled + ",idle:" + idle + ",autoComplete:" + autoComplete
               + ",next:" + (nextMissionOnFailed ?? "null")
               + ",steps:[" + string.Join(",", steps.Select(step => step.SerializeLikeTheGame()))
               + "],rewards:[" + string.Join(",", rewards.Select(reward => reward.ToJson())) + "]}";
        public string name = "Test mission";
        public string? description, category, completionText, sourceName, iconName;
        public MissionDifficulty difficulty;
        public Source.Galaxy.Faction? sourceFaction;
        public Source.Galaxy.MapPointOfInterest? sourcePoi;
        public Source.Galaxy.MapPointOfInterest? turnIn;
        public bool canAbandon, dynamicLevel, trackedOnHud, canBeIdled, idle, autoComplete;
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
            => "{" + description + ":" + requireAllObjectives + ":hidden=" + hidden + ":["
               + string.Join(",", objectives.Select(objective => ((MissionObjective)objective).ToJson())) + "]}";
    }
}
namespace Source.MissionSystem.Objectives
{
    public enum ItemCategory { Ore, Salvage }
    public class Mining { public ItemCategory? itemCategory; }
    public class Salvage : Mining { }
}

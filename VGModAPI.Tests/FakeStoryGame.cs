using System;
using System.Collections.Generic;
using System.Linq;

// Reflection doubles shaped like the installed assembly's story surface, so the PRODUCTION bindings
// and adapter can be exercised on the host. They deliberately reproduce the behaviour the adapter
// depends on, including the two that make missions dangerous to get wrong: the game writes
// sourceFaction.identifier unconditionally when it saves, and it refuses a story identifier that is
// already active or archived. The installed-assembly Cecil pins remain the authority for the SHAPE;
// these doubles are how the code that uses that shape is executed.
namespace Source.Galaxy
{
    public sealed class Faction
    {
        public static readonly Dictionary<string, Faction> allFactions = new(StringComparer.Ordinal)
        {
            ["tradingGuild"] = new Faction("tradingGuild", "Trading Guild"),
            ["miningGuild"] = new Faction("miningGuild", "Mining Guild")
        };
        private Faction(string id, string label) { identifier = id; name = label; }
        public string identifier { get; }
        public string name { get; }
        public static Faction? Get(string id) => id != null && allFactions.TryGetValue(id, out var faction) ? faction : null;
    }
}

namespace Source.MissionSystem
{
    public enum MissionDifficulty { Easy, Normal, Hard, Skull, Insane, Faction, Tutorial, Story }

    public abstract class MissionObjective
    {
        public static MissionObjective? Create(string type)
            => Type.GetType("Source.MissionSystem.Objectives." + type)?.GetConstructor(Type.EmptyTypes)?.Invoke(null) as MissionObjective;
        /// <summary>Mirrors the game writing each objective's own data; a null dependency throws here too.</summary>
        public abstract string ToJson();
    }

    public abstract class MissionReward
    {
        public static MissionReward? Create(string type)
            => Type.GetType("Source.MissionSystem.Rewards." + type)?.GetConstructor(Type.EmptyTypes)?.Invoke(null) as MissionReward;
        public abstract string ToJson();
    }

    public sealed class StoryMission
    {
        public delegate Mission CreateMission(Source.Player.GamePlayer player);
        public static readonly Dictionary<string, StoryMission> allMissions = new(StringComparer.Ordinal);
        public string identifier;
        public string pickupHint;
        public CreateMission generator;
        public Func<Source.Player.GamePlayer, bool>? checkAvailable;
        public StoryMission(string identifier, CreateMission generator, Func<Source.Player.GamePlayer, bool>? checkAvailable, string pickupHint)
        { this.identifier = identifier; this.generator = generator; this.checkAvailable = checkAvailable; this.pickupHint = pickupHint; }
        /// <summary>Vanilla REPLACES a duplicate identifier; the API's collision policy exists because of this.</summary>
        public static void Add(StoryMission mission) => allMissions[mission.identifier] = mission;
        public static Mission Get(Source.Player.GamePlayer player, string identifier)
        {
            var mission = allMissions[identifier].generator(player);   // Throws for an unknown identifier, as vanilla does.
            mission.storyId = identifier;
            return mission;
        }
    }
}

namespace Source.MissionSystem.Objectives
{
    public sealed class TravelToPOI : MissionObjective
    {
        public string? targetPOI;
        public float requiredVisitTime;
        public override string ToJson() => "{travel:" + targetPOI + ":" + requiredVisitTime + "}";
    }
    public sealed class KillEnemies : MissionObjective
    {
        public string? shipType;
        public Source.Galaxy.Faction? enemyFaction;
        public int requiredAmount;
        /// <summary>Exactly the game's dependency: a null enemy faction throws while saving.</summary>
        public override string ToJson() => "{kill:" + enemyFaction!.identifier + ":" + requiredAmount + "}";
    }
    public sealed class CollectCredits : MissionObjective
    {
        public int requiredAmount;
        public override string ToJson() => "{credits:" + requiredAmount + "}";
    }
}

namespace Source.MissionSystem.Rewards
{
    public sealed class Credits : MissionReward
    {
        public int amount, baseAmount;
        public override string ToJson() => "{credits:" + amount + "/" + baseAmount + "}";
    }
    public sealed class Experience : MissionReward
    {
        public int amount, baseAmount;
        public override string ToJson() => "{xp:" + amount + "/" + baseAmount + "}";
    }
}

namespace Source.Player
{
    public sealed partial class GamePlayer
    {
        public List<string> AcceptanceLog { get; } = new();
        /// <summary>force:true skips the duplicate guard, exactly as the game does.</summary>
        public void AddMissionWithLog(Source.MissionSystem.Mission mission, bool force)
        {
            if (!force && mission.storyId != null && HasStoryMission(mission.storyId))
            { AcceptanceLog.Add("skipped:" + mission.storyId); return; }
            missions.Add(mission);
            AcceptanceLog.Add("added:" + mission.storyId);
        }
        public bool HasStoryMission(string id)
            => missions.Any(mission => mission.storyId == id) || missionsArchive.Contains(id);
        public Source.MissionSystem.Mission? GetActiveStoryMission(string id)
            => missions.FirstOrDefault(mission => mission.storyId == id);
        /// <summary>completed:true archives the story identifier; completed:false abandons without archiving.</summary>
        public void RemoveMission(Source.MissionSystem.Mission mission, bool completed)
        {
            missions.Remove(mission);
            if (completed && mission.storyId != null) missionsArchive.Add(mission.storyId);
            AcceptanceLog.Add((completed ? "completed:" : "abandoned:") + mission.storyId);
        }
    }
}

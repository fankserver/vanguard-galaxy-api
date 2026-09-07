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
    /// <summary>
    /// The game's own lookup NEVER returns null: it resolves Source.Galaxy.Factions.&lt;identifier&gt;,
    /// constructs it and registers it, and throws for anything else. The double behaves the same way,
    /// with the game's real identifiers, which are those PascalCase type names.
    /// </summary>
    public class Faction
    {
        public static readonly Dictionary<string, Faction> allFactions = new(StringComparer.Ordinal);
        public string identifier { get; internal set; } = "";
        public string name { get; internal set; } = "";
        public static Faction Get(string id)
        {
            if (allFactions.TryGetValue(id, out var known)) return known;
            var created = Create(id);
            allFactions[id] = created;
            return created;
        }
        private static Faction Create(string id)
        {
            // The game resolves the type by name in its own assembly; the double does the same in this one.
            var type = typeof(Faction).Assembly.GetType("Source.Galaxy.Factions." + id);
            var faction = (Faction)type!.GetConstructor(Type.EmptyTypes)!.Invoke(null);   // NRE for an unknown identity, as the game does
            faction.identifier = id;
            faction.name = id;
            return faction;
        }
    }
}

namespace Source.Galaxy.Factions
{
    public sealed class TradingGuild : Source.Galaxy.Faction { }
    public sealed class MiningGuild : Source.Galaxy.Faction { }
    public sealed class Player : Source.Galaxy.Faction { }
}

namespace Source.MissionSystem
{
    public enum MissionDifficulty { Easy, Normal, Hard, Skull, Insane, Faction, Tutorial, Story }

    public enum MissionTrigger { Travel, Kill, Credits, None }

    public abstract class MissionObjective
    {
        /// <summary>The virtual dispatch point the game uses to advance objectives, and the guard patches.</summary>
        public virtual void ProcessMissionTrigger(MissionTrigger trigger, object data) => Progress++;
        public int Progress { get; private set; }
        public virtual bool IsComplete() => false;
        public static Action? DuringCreate;
        public static MissionObjective? Create(string type)
        {
            DuringCreate?.Invoke();
            return Type.GetType("Source.MissionSystem.Objectives." + type)?.GetConstructor(Type.EmptyTypes)?.Invoke(null) as MissionObjective;
        }
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
        public Func<bool>? Completion;
        public override bool IsComplete() => Completion?.Invoke() ?? false;
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
    public sealed class TriggerObjective : MissionObjective
    {
        public MissionTrigger trigger;
        public string? description;
        public int requiredAmount = 1;
        public int currentAmount;
        public override bool IsComplete() => currentAmount >= requiredAmount;
        public override void ProcessMissionTrigger(MissionTrigger trigger, object data)
            => currentAmount = Math.Min(currentAmount + (data is int count ? count : 1), requiredAmount);
        public override string ToJson() => "{trigger:" + trigger + ":" + description + ":" + currentAmount + ":" + requiredAmount + "}";
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

namespace Behaviour.UI.Missions
{
    /// <summary>
    /// The route the game's own abandon/retry button takes, reproduced from the installed IL: remove
    /// the mission, and for a retryable story mission re-add the SAME identifier out of the catalog.
    /// The catalog lookup throws for an absent entry, exactly as the game's does.
    /// </summary>
    public sealed class MissionDetails
    {
        public bool Retryable;
        public void AbandonMission(Source.MissionSystem.Mission mission)
        {
            var identifier = mission.nextMissionOnFailed ?? mission.storyId;
            bool retry = Retryable;
            Source.Player.GamePlayer.current!.RemoveMission(mission, false);
            if (retry)
                Source.Player.GamePlayer.current.AddMissionWithLog(
                    Source.MissionSystem.StoryMission.Get(Source.Player.GamePlayer.current, identifier!), true);
        }
    }
}

namespace Source.Player
{
    public sealed partial class GamePlayer
    {
        public List<string> AcceptanceLog { get; } = new();
        public long credits { get; set; }
        /// <summary>The game's own capacity limit and the check AcceptMission makes with it.</summary>
        public static int MissionLimit = 20;
        public bool IsMissionsLimitExceeded() => missions.Count >= MissionLimit;
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

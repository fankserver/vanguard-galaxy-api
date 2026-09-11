using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

/// <summary>Native unit rank, validated against the installed game at spawn time.</summary>
public enum EncounterRank { Rookie, Standard, Veteran, Elite, Champion, Commander, Legendary }

/// <summary>One reinforcement wave: exactly <see cref="Count"/> ships of one exact class after a delay.</summary>
public sealed class EncounterWave
{
    public float DelaySeconds { get; }
    public string ShipClassId { get; }
    public int Count { get; }
    public EncounterWave(float delaySeconds, string shipClassId, int count)
    {
        if (float.IsNaN(delaySeconds) || delaySeconds < 0 || delaySeconds > 3600) throw new ArgumentOutOfRangeException(nameof(delaySeconds));
        if (string.IsNullOrWhiteSpace(shipClassId)) throw new ArgumentException("An exact ship class is required.", nameof(shipClassId));
        if (count < 1 || count > 50) throw new ArgumentOutOfRangeException(nameof(count));
        DelaySeconds = delaySeconds; ShipClassId = shipClassId; Count = count;
    }
}

/// <summary>
/// A deterministic owned encounter: the exact ship counts the encounter was designed around,
/// delivered through the game's timed-reinforcement trigger (point-budgeted payloads under-place
/// large requests, so exactness is the contract here). Hostility is scoped to the spawned units
/// only — an existing faction's diplomacy is never modified.
/// </summary>
public sealed class EncounterComposition
{
    public IReadOnlyList<EncounterWave> Waves { get; }
    public string FactionId { get; }
    public int Level { get; }
    public EncounterRank Rank { get; }
    /// <summary>Spawned units attack the player on sight without changing their faction's diplomacy.</summary>
    public bool HostileToPlayer { get; }
    /// <summary>Killing the spawned units costs no reputation with their faction. Only meaningful with hostility.</summary>
    public bool NoReputationLoss { get; }
    public EncounterComposition(IEnumerable<EncounterWave> waves, string factionId, int level,
        EncounterRank rank = EncounterRank.Standard, bool hostileToPlayer = false, bool noReputationLoss = true)
    {
        if (waves == null) throw new ArgumentNullException(nameof(waves));
        var copied = waves.ToArray();
        if (copied.Length < 1 || copied.Length > 32 || copied.Any(wave => wave == null)) throw new ArgumentException("1-32 waves are required.", nameof(waves));
        if (copied.Sum(wave => wave.Count) > 200) throw new ArgumentException("At most 200 ships per composition.", nameof(waves));
        if (string.IsNullOrWhiteSpace(factionId)) throw new ArgumentException("An existing faction identity is required.", nameof(factionId));
        if (level < 1 || level > 100000) throw new ArgumentOutOfRangeException(nameof(level));
        if (!Enum.IsDefined(typeof(EncounterRank), rank)) throw new ArgumentOutOfRangeException(nameof(rank));
        Waves = new ReadOnlyCollection<EncounterWave>(copied);
        FactionId = factionId; Level = level; Rank = rank;
        HostileToPlayer = hostileToPlayer; NoReputationLoss = noReputationLoss;
    }
}

/// <summary>Retained outcome of one encounter spawn request. Scheduled units are transient session content, never persisted or replayed by the API.</summary>
public sealed class EncounterSpawnResult
{
    public WorldContentStatus Status { get; }
    public string Detail { get; }
    /// <summary>Units actually scheduled through the native trigger; equals the composition total on success.</summary>
    public int ScheduledUnits { get; }
    public bool Succeeded => Status == WorldContentStatus.Succeeded;
    public EncounterSpawnResult(WorldContentStatus status, int scheduledUnits = 0, string detail = "")
    { Status = status; ScheduledUnits = scheduledUnits; Detail = detail ?? ""; }
}

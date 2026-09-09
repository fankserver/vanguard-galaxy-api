using System;

namespace VGModAPI;

public enum BoardingRuleScope { Ships, Installations, Both }
public enum BoardingDisableDecision { Vanilla, Allow, Deny }
public enum BoardingDamageCause { Unspecified, Combat, Ammunition, Hazard, Grenade, Scuttle, HostDestroyed, ReactorExplosion }

/// <summary>Evaluation at a structurally eligible native damage boundary. Allow does not bypass structural exclusions.</summary>
public sealed class BoardingDisableContext
{
    public Guid SessionId { get; }
    public float Hull { get; }
    public float MaximumHull { get; }
    public float EmpCharge { get; }
    public float HullFraction => Hull / MaximumHull;
    public BoardingDisableContext(Guid sessionId, float hull, float maximumHull, float empCharge)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session required.");
        BoardingRuleNumbers.Nonnegative(hull); BoardingRuleNumbers.Nonnegative(maximumHull); BoardingRuleNumbers.Nonnegative(empCharge);
        if (maximumHull == 0) throw new ArgumentOutOfRangeException(nameof(maximumHull));
        SessionId = sessionId; Hull = hull; MaximumHull = maximumHull; EmpCharge = empCharge;
    }
}

/// <summary>Creation-time configuration, also evaluated for pre-entry estimates. Callbacks must be pure.</summary>
public sealed class BoardingEncounterContext
{
    public Guid SessionId { get; }
    public BoardingEncounterKind Kind { get; }
    public int Level { get; }
    public BoardingEncounterContext(Guid sessionId, BoardingEncounterKind kind, int level)
    {
        if (sessionId == Guid.Empty || !Enum.IsDefined(typeof(BoardingEncounterKind), kind) || level < 0) throw new ArgumentException("Invalid encounter context.");
        SessionId = sessionId; Kind = kind; Level = level;
    }
}

/// <summary>Multipliers apply once before initial placement. Resumed native saved values are authoritative.</summary>
public sealed class BoardingEncounterTuning
{
    public float DefenderPowerMultiplier { get; }
    public float DefenderHealthMultiplier { get; }
    public BoardingEncounterTuning(float defenderPowerMultiplier = 1, float defenderHealthMultiplier = 1)
    {
        BoardingRuleNumbers.Multiplier(defenderPowerMultiplier); BoardingRuleNumbers.Multiplier(defenderHealthMultiplier);
        DefenderPowerMultiplier = defenderPowerMultiplier; DefenderHealthMultiplier = defenderHealthMultiplier;
    }
}

/// <summary>Damage before application. HostDestroyed is authoritative and cannot be reduced by registered rules.</summary>
public sealed class BoardingIntegrityContext
{
    public BoardingEncounterContext Encounter { get; }
    public BoardingDamageCause Cause { get; }
    public float Amount { get; }
    public float Integrity { get; }
    public BoardingIntegrityContext(BoardingEncounterContext encounter, BoardingDamageCause cause, float amount, float integrity)
    {
        Encounter = encounter ?? throw new ArgumentNullException(nameof(encounter));
        if (!Enum.IsDefined(typeof(BoardingDamageCause), cause)) throw new ArgumentOutOfRangeException(nameof(cause));
        BoardingRuleNumbers.Nonnegative(amount); BoardingRuleNumbers.Nonnegative(integrity);
        Cause = cause; Amount = amount; Integrity = integrity;
    }
}

/// <summary>Provider-scoped registrations. Dispose removes all this instance's registrations, not another provider's rules.</summary>
public interface IBoardingRuleProvider : IDisposable
{
    IDisposable RegisterDisable(string localId, Func<BoardingDisableContext, BoardingDisableDecision> evaluate, int priority = 0);
    /// <summary>Optional per-damage-boundary probability in [0,1]; null preserves vanilla. Highest priority wins; conflicting ties preserve vanilla.</summary>
    IDisposable RegisterDisableChance(string localId, Func<BoardingDisableContext, float?> probability, int priority = 0);
    IDisposable RegisterExplosion(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, bool> allow, int priority = 0);
    IDisposable RegisterEncounter(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, BoardingEncounterTuning> evaluate, int priority = 0);
    IDisposable RegisterIntegrity(string localId, BoardingRuleScope scope, Func<BoardingIntegrityContext, float> multiplier, int priority = 0);
    IDisposable RegisterScuttle(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, bool> allow, int priority = 0);
}

/// <summary>Main-thread-only short synchronous policies, not observers or a permission to call commands reentrantly.</summary>
public interface IBoardingRuleService : IServiceStatus
{
    bool IsEvaluating { get; }
    IBoardingRuleProvider AcquireProvider(string pluginId);
}

internal static class BoardingRuleNumbers
{
    internal static void Nonnegative(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    }
    internal static void Multiplier(float value)
    {
        Nonnegative(value); if (value > 10) throw new ArgumentOutOfRangeException(nameof(value), "Individual multipliers must be between 0 and 10.");
    }
}

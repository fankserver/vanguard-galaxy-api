using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

public enum BoardingTacticalAction
{
    Move, ClearMovement, RetreatFromCompartment, SetPriority, ClearPriority, Unlock,
    ToggleBarricade, ThrowGrenade, AcceptBuyout, DeclineBuyout, RequestExtraction, ConfirmExtraction
}
public enum BoardingMovementFilter { Any, Combat, Specialist }
public enum BoardingCombatSide { Attackers, Defenders }
public enum BoardingCombatPolicyKind { Power, InitialHealth, Morale, CasualtyRate, Surrender, Defection, Reinforcement, Hazard, Venting }

/// <summary>A copied request, not permission. Compartment and crew identity must be validated against the current operation.</summary>
public sealed class BoardingTacticalRequest
{
    public BoardingTacticalAction Action { get; }
    public int? Compartment { get; }
    public BoardingMovementFilter Filter { get; }
    public int Count { get; }
    public bool AllowFriendlyDamage { get; }
    public BoardingTacticalRequest(BoardingTacticalAction action, int? compartment = null,
        BoardingMovementFilter filter = BoardingMovementFilter.Any, int count = 1, bool allowFriendlyDamage = false)
    {
        if (!Enum.IsDefined(typeof(BoardingTacticalAction), action) || !Enum.IsDefined(typeof(BoardingMovementFilter), filter)) throw new ArgumentException("Unknown tactical request.");
        if (compartment < 0 || count < 1) throw new ArgumentOutOfRangeException(nameof(compartment));
        Action = action; Compartment = compartment; Filter = filter; Count = count; AllowFriendlyDamage = allowFriendlyDamage;
    }
}

/// <summary>Copied discovered tactical state; availability can change before execution.</summary>
public sealed class BoardingTacticalSnapshot
{
    public BoardingHandle Operation { get; }
    public IReadOnlyList<BoardingCompartmentSnapshot> Compartments { get; }
    public int GrenadeCharges { get; }
    public float GrenadeCooldown { get; }
    public bool CanRequestExtraction { get; }
    public bool AwaitingExtraction { get; }
    public BoardingTacticalSnapshot(BoardingHandle operation, IEnumerable<BoardingCompartmentSnapshot> compartments,
        int grenadeCharges, float grenadeCooldown, bool canRequestExtraction, bool awaitingExtraction)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        if (grenadeCharges < 0 || float.IsNaN(grenadeCooldown) || float.IsInfinity(grenadeCooldown) || grenadeCooldown < 0) throw new ArgumentOutOfRangeException(nameof(grenadeCharges));
        var copy = (compartments ?? throw new ArgumentNullException(nameof(compartments))).ToArray();
        if (copy.Any(c => c == null) || copy.Select(c => c.Index).Distinct().Count() != copy.Length) throw new ArgumentException("Invalid compartments.");
        Compartments = new ReadOnlyCollection<BoardingCompartmentSnapshot>(copy);
        GrenadeCharges = grenadeCharges; GrenadeCooldown = grenadeCooldown; CanRequestExtraction = canRequestExtraction; AwaitingExtraction = awaitingExtraction;
    }
}

/// <summary>Immutable evaluation context; a policy receives no mutable native simulation or crew object.</summary>
public sealed class BoardingCombatContext
{
    public BoardingEncounterContext Encounter { get; }
    public BoardingCombatPolicyKind Kind { get; }
    public BoardingCombatSide Side { get; }
    public int? Compartment { get; }
    public float Value { get; }
    public BoardingCombatContext(BoardingEncounterContext encounter, BoardingCombatPolicyKind kind, BoardingCombatSide side, int? compartment, float value)
    {
        Encounter = encounter ?? throw new ArgumentNullException(nameof(encounter));
        if (!Enum.IsDefined(typeof(BoardingCombatPolicyKind), kind) || !Enum.IsDefined(typeof(BoardingCombatSide), side)) throw new ArgumentException("Unknown combat policy context.");
        if (compartment < 0 || float.IsNaN(value) || float.IsInfinity(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Kind = kind; Side = side; Compartment = compartment; Value = value;
    }
}

public interface IBoardingTacticalService : IServiceStatus
{
    BoardingTacticalSnapshot? GetSnapshot(BoardingHandle operation);
    BoardingCommandResult Execute(IBoardingController controller, BoardingTacticalRequest request);
}

/// <summary>Provider-instance ownership is independent of exclusive command control.</summary>
public interface IBoardingCombatProvider : IDisposable
{
    IDisposable RegisterMultiplier(string localId, BoardingRuleScope scope, BoardingCombatPolicyKind kind,
        Func<BoardingCombatContext, float> multiplier, int priority = 0);
    IDisposable RegisterVeto(string localId, BoardingRuleScope scope, BoardingCombatPolicyKind kind,
        Func<BoardingCombatContext, bool> allow, int priority = 0);
}
public interface IBoardingCombatService : IServiceStatus
{
    bool IsEvaluating { get; }
    IBoardingCombatProvider AcquireProvider(string pluginId);
}

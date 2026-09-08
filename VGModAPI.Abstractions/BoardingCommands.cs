using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

public enum BoardingCommandStatus
{
    Admitted, IntegrationUnavailable, SessionUnavailable, StaleHandle, Busy, ControlConflict,
    WrongPhase, Travelling, TargetUnavailable, InsufficientCrew, CapacityExceeded,
    InvalidCrew, InvalidOptions, FactionConsentRequired, OperationExists, NativeFailure,
    InvalidAction, NotDiscovered, MissingSpecialist, InsufficientResources, FriendlyDamageConsentRequired
}

public enum BoardingAmmunition { Standard, Hollow, ArmourPiercing }
public enum BoardingStealth { Silent, Balanced, Aggressive }

/// <summary>Copied desired crew. Execution revalidates the actual ship roster; this is not a reservation.</summary>
public sealed class BoardingCrewManifest
{
    public IReadOnlyDictionary<string, int> Crew { get; }
    public int Count { get; }
    public BoardingCrewManifest(IEnumerable<KeyValuePair<string, int>> crew)
    {
        if (crew == null) throw new ArgumentNullException(nameof(crew));
        var copy = new Dictionary<string, int>(StringComparer.Ordinal);
        long total = 0;
        foreach (var pair in crew)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value <= 0) throw new ArgumentException("Crew identifiers and positive counts required.", nameof(crew));
            copy.Add(pair.Key, pair.Value); total += pair.Value;
            if (total > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(crew));
        }
        if (total == 0) throw new ArgumentException("At least one crew member required.", nameof(crew));
        Crew = new ReadOnlyDictionary<string, int>(copy); Count = (int)total;
    }
}

/// <summary>Supported native options. Automatic buyout may spend credits later; faction consent is separate.</summary>
public sealed class BoardingCommandOptions
{
    public BoardingAmmunition Ammunition { get; }
    public BoardingStealth Stealth { get; }
    public bool AutoMove { get; }
    public bool AutoAcceptBuyout { get; }
    public BoardingCommandOptions(BoardingAmmunition ammunition = BoardingAmmunition.Standard,
        BoardingStealth stealth = BoardingStealth.Balanced, bool autoMove = false, bool autoAcceptBuyout = false)
    {
        if (!Enum.IsDefined(typeof(BoardingAmmunition), ammunition) || !Enum.IsDefined(typeof(BoardingStealth), stealth))
            throw new ArgumentException("Unknown boarding option.");
        Ammunition = ammunition; Stealth = stealth; AutoMove = autoMove; AutoAcceptBuyout = autoAcceptBuyout;
    }
}

/// <summary>Admitted is not arrival, victory, delivery or settlement. NativeFailure can include partial native effects.</summary>
public sealed class BoardingCommandResult
{
    public BoardingCommandStatus Status { get; }
    public BoardingHandle? Operation { get; }
    public string Detail { get; }
    public bool Admitted => Status == BoardingCommandStatus.Admitted;
    public BoardingCommandResult(BoardingCommandStatus status, string detail, BoardingHandle? operation = null)
    {
        if (!Enum.IsDefined(typeof(BoardingCommandStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); Operation = operation;
    }
}

/// <summary>One instance's exclusive mod control. Native manual takeover can revoke it; observation does not acquire control.</summary>
public interface IBoardingController : IDisposable
{
    bool IsActive { get; }
    BoardingHandle Target { get; }
    BoardingCommandResult Start(BoardingCrewManifest crew, BoardingCommandOptions options, bool allowFactionConsequences = false);
    BoardingCommandResult Resume();
    BoardingCommandResult Reinforce(BoardingCrewManifest crew);
    BoardingCommandResult CancelApproach();
    BoardingCommandResult Retreat();
    BoardingCommandResult RequestExtraction();
    BoardingCommandResult ConfirmExtraction();
    BoardingCommandResult SetOptions(BoardingCommandOptions options);
}

/// <summary>Main-thread command entry. Control is runtime-instance scoped and invalidated by target/session replacement.</summary>
public interface IBoardingCommands
{
    BoardingCommandResult AcquireControl(string pluginId, BoardingHandle target, out IBoardingController? controller);
}

using System;
using System.Collections.Generic;

namespace VGModAPI;

/// <summary>Issued station identity scoped to one loaded session; not a persisted station ID.</summary>
public sealed class RecipeStationHandle : IEquatable<RecipeStationHandle>
{
    public Guid SessionId { get; }
    public Guid InstanceId { get; }
    public string DisplayName { get; }
    public RecipeStationHandle(Guid sessionId, Guid instanceId, string displayName)
    {
        if (sessionId == Guid.Empty || instanceId == Guid.Empty) throw new ArgumentException("Nonempty session and station identities required.");
        SessionId = sessionId; InstanceId = instanceId; DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
    }
    public bool Equals(RecipeStationHandle? other) => other != null && SessionId == other.SessionId && InstanceId == other.InstanceId;
    public override bool Equals(object? obj) => obj is RecipeStationHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(SessionId, InstanceId);
}

public enum RecipeQuoteStatus { Available, IntegrationUnavailable, SessionUnavailable, StationUnavailable, StaleHandle, InvalidRequest, RecipeUnavailable, Unsupported, NativeFailure, LimitExceeded }
public enum RecipeBlocker { InsufficientCredits, MissingIngredients, QueueFull, InventoryUnavailable, OutputUnresolved, AutomaticRefiningDisabled, PricingUnavailable }
/// <summary>Native inventory purposes are distinct even when their balances belong to the same player.</summary>
public enum RecipeInventoryKind { PlayerRefinedMaterials, StationMaterials, PlayerArmory, PlayerData, ShipCargo }
public enum RefineryInputPolicy { Manual, AutomaticSelection }

/// <summary>A player's resource balance in one inventory purpose/location. Null amount is unknown or inaccessible, never zero.</summary>
public sealed class RecipeInventoryBalance
{
    public RecipeInventoryKind Kind { get; }
    public RecipeStationHandle? Station { get; }
    public bool Accessible { get; }
    public double? Amount { get; }
    public RecipeInventoryBalance(RecipeInventoryKind kind, RecipeStationHandle? station, bool accessible, double? amount)
    {
        if (!Enum.IsDefined(typeof(RecipeInventoryKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (amount.HasValue && (double.IsNaN(amount.Value) || double.IsInfinity(amount.Value) || amount.Value < 0)) throw new ArgumentOutOfRangeException(nameof(amount));
        if (!accessible && amount.HasValue) throw new ArgumentException("Inaccessible balances must not imply usable stock.");
        if ((kind == RecipeInventoryKind.StationMaterials) != (station != null)) throw new ArgumentException("Only station material balances carry a station handle.");
        Kind = kind; Station = station; Accessible = accessible; Amount = amount;
    }
}

public sealed class RecipeIngredientRequirement
{
    public RecipeResourceId Resource { get; }
    public double Required { get; }
    public double? Available { get; }
    public double? Missing => Available.HasValue ? Math.Max(0, Required - Available.Value) : null;
    public IReadOnlyList<RecipeInventoryBalance> Inventories { get; }
    public RecipeIngredientRequirement(RecipeResourceId resource, double required, IEnumerable<RecipeInventoryBalance> inventories)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        if (double.IsNaN(required) || double.IsInfinity(required) || required < 0) throw new ArgumentOutOfRangeException(nameof(required));
        Required = required; Inventories = RecipeValues.Copy(inventories, 16);
        double? amount = Inventories.Count == 0 ? null : 0;
        foreach (var inventory in Inventories)
        {
            if (!inventory.Accessible) continue;
            if (!inventory.Amount.HasValue) { amount = null; break; }
            amount += inventory.Amount.Value;
            if (amount.HasValue && double.IsInfinity(amount.Value)) throw new ArgumentOutOfRangeException(nameof(inventories));
        }
        Available = amount;
    }
}

/// <summary>Expected output, not an observed delivery. Null resource means the bonus item identity cannot be predicted.</summary>
public sealed class RecipeOutputPreview
{
    public RecipeResourceId? Resource { get; }
    public double Amount { get; }
    /// <summary>Chance per completed batch. Amount is the total if this outcome occurs for every requested batch.</summary>
    public double ProbabilityPerBatch { get; }
    public string Detail { get; }
    public IReadOnlyList<RecipeInventoryKind> PossibleDestinations { get; }
    public RecipeOutputPreview(RecipeResourceId? resource, double amount, double probability, string detail, IEnumerable<RecipeInventoryKind> possibleDestinations)
    {
        if (double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (double.IsNaN(probability) || probability < 0 || probability > 1) throw new ArgumentOutOfRangeException(nameof(probability));
        Resource = resource; Amount = amount; ProbabilityPerBatch = probability; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        var destinations = new List<RecipeInventoryKind>();
        foreach (var destination in possibleDestinations ?? throw new ArgumentNullException(nameof(possibleDestinations)))
        {
            if (!Enum.IsDefined(typeof(RecipeInventoryKind), destination) || destinations.Count >= 16) throw new ArgumentOutOfRangeException(nameof(possibleDestinations));
            destinations.Add(destination);
        }
        PossibleDestinations = destinations.AsReadOnly();
    }
}

/// <summary>Immutable advisory quote. Revision identifies this read, not a reservation or permission to execute later.</summary>
public sealed class RecipeQuote
{
    public RecipeQuoteStatus Status { get; }
    public string Detail { get; }
    public RecipeStationHandle? Station { get; }
    public RecipeId Recipe { get; }
    public int Batches { get; }
    public long Revision { get; }
    public RefineryInputPolicy RefineryPolicy { get; }
    /// <summary>Level supplied to native builders; fixed item outputs retain their own inherent level.</summary>
    public int? OutputLevel { get; }
    public long? CreditsRequired { get; }
    public long? CreditsAvailable { get; }
    public double? SecondsPerBatch { get; }
    public int? QueueUsed { get; }
    public int? QueueCapacity { get; }
    public IReadOnlyList<RecipeIngredientRequirement> Inputs { get; }
    public IReadOnlyList<RecipeOutputPreview> Outputs { get; }
    public IReadOnlyList<RecipeBlocker> Blockers { get; }
    public bool RequirementsMet => Status == RecipeQuoteStatus.Available && Blockers.Count == 0;
    public RecipeQuote(RecipeQuoteStatus status, string detail, RecipeStationHandle? station, RecipeId recipe, int batches, long revision,
        IEnumerable<RecipeIngredientRequirement> inputs, IEnumerable<RecipeOutputPreview> outputs, IEnumerable<RecipeBlocker> blockers,
        int? outputLevel = null, long? creditsRequired = null, long? creditsAvailable = null, double? secondsPerBatch = null, int? queueUsed = null, int? queueCapacity = null, RefineryInputPolicy refineryPolicy = RefineryInputPolicy.Manual)
    {
        if (!Enum.IsDefined(typeof(RecipeQuoteStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (revision < 0 || creditsRequired < 0 || creditsAvailable < 0 || queueUsed < 0 || queueCapacity < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (secondsPerBatch.HasValue && (double.IsNaN(secondsPerBatch.Value) || double.IsInfinity(secondsPerBatch.Value) || secondsPerBatch.Value <= 0)) throw new ArgumentOutOfRangeException(nameof(secondsPerBatch));
        if (status == RecipeQuoteStatus.Available && (station == null || batches < 1 || revision < 1 || !creditsAvailable.HasValue))
            throw new ArgumentException("An available quote requires station, revision, positive batches and known player credits.");
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); Station = station;
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe)); Batches = batches; Revision = revision;
        Inputs = RecipeValues.Copy(inputs, 256); Outputs = RecipeValues.Copy(outputs, 512);
        var copy = new List<RecipeBlocker>();
        foreach (var blocker in blockers ?? throw new ArgumentNullException(nameof(blockers)))
        {
            if (!Enum.IsDefined(typeof(RecipeBlocker), blocker) || copy.Count >= 16) throw new ArgumentOutOfRangeException(nameof(blockers));
            if (!copy.Contains(blocker)) copy.Add(blocker);
        }
        if (status == RecipeQuoteStatus.Available && !creditsRequired.HasValue && !copy.Contains(RecipeBlocker.PricingUnavailable))
            throw new ArgumentException("Unknown price requires a PricingUnavailable blocker.");
        Blockers = copy.AsReadOnly(); OutputLevel = outputLevel; CreditsRequired = creditsRequired; CreditsAvailable = creditsAvailable;
        if (!Enum.IsDefined(typeof(RefineryInputPolicy), refineryPolicy)) throw new ArgumentOutOfRangeException(nameof(refineryPolicy));
        RefineryPolicy = refineryPolicy;
        SecondsPerBatch = secondsPerBatch; QueueUsed = queueUsed; QueueCapacity = queueCapacity;
    }
}

/// <summary>Read-only, main-thread station requirements. Requery immediately before any separately supported action.</summary>
public interface IRecipeQuotes
{
    RecipeStationHandle? CurrentStation { get; }
    RecipeQuote Quote(RecipeStationHandle station, RecipeId recipe, int batches = 1, RefineryInputPolicy refineryPolicy = RefineryInputPolicy.Manual);
    /// <summary>Canister extraction is immediate native conversion, not a queued job.</summary>
    RecipeQuote QuoteMaterialExtraction(RecipeStationHandle station, RecipeResourceId material, int count = 1);
}

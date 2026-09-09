using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum CraftingJobState { Active, Finished, Cancelled, Invalidated }
public enum CraftingJobQueryStatus { Available, IntegrationUnavailable, SessionUnavailable, StaleHandle, NativeFailure, LimitExceeded }
public enum CraftingJobEventKind { Queued, BatchObserved, Finished, Cancelled, OperationFaulted, Invalidated }
public enum CraftingDeliveryStatus { Verified, Unresolved, NotApplicable }

/// <summary>Issued identity for a job at one station in one runtime session. Not a persistent save key.</summary>
public sealed class CraftingJobHandle : IEquatable<CraftingJobHandle>
{
    public RecipeStationHandle Station { get; }
    public Guid InstanceId { get; }
    public CraftingJobHandle(RecipeStationHandle station, Guid instanceId)
    {
        Station = station ?? throw new ArgumentNullException(nameof(station));
        if (instanceId == Guid.Empty) throw new ArgumentException("Nonempty job identity required.", nameof(instanceId));
        InstanceId = instanceId;
    }
    public bool Equals(CraftingJobHandle? other) => other != null && Station.Equals(other.Station) && InstanceId == other.InstanceId;
    public override bool Equals(object? obj) => obj is CraftingJobHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Station, InstanceId);
}

/// <summary>Copied native progress. Finished means native processing ended, not proof every output reached storage.</summary>
public sealed class CraftingJobSnapshot
{
    public CraftingJobHandle Handle { get; }
    public RecipeId Recipe { get; }
    public RecipeProcess Process { get; }
    public CraftingJobState State { get; }
    public int InitialBatches { get; }
    public int RemainingBatches { get; }
    public int? CraftedLevel { get; }
    public double? ProgressRatio { get; }
    public double? SecondsPerBatch { get; }
    public CraftingJobSnapshot(CraftingJobHandle handle, RecipeId recipe, RecipeProcess process, CraftingJobState state,
        int initialBatches, int remainingBatches, int? craftedLevel, double? progressRatio, double? secondsPerBatch)
    {
        Handle = handle ?? throw new ArgumentNullException(nameof(handle)); Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (process is not (RecipeProcess.Forge or RecipeProcess.Refining)) throw new ArgumentOutOfRangeException(nameof(process));
        if (!Enum.IsDefined(typeof(CraftingJobState), state)) throw new ArgumentOutOfRangeException(nameof(state));
        if (initialBatches < 1 || remainingBatches < 0 || remainingBatches > initialBatches) throw new ArgumentOutOfRangeException(nameof(remainingBatches));
        if (progressRatio.HasValue && (double.IsNaN(progressRatio.Value) || double.IsInfinity(progressRatio.Value) || progressRatio < 0 || progressRatio > 1)) throw new ArgumentOutOfRangeException(nameof(progressRatio));
        if (secondsPerBatch.HasValue && (double.IsNaN(secondsPerBatch.Value) || double.IsInfinity(secondsPerBatch.Value) || secondsPerBatch <= 0)) throw new ArgumentOutOfRangeException(nameof(secondsPerBatch));
        Process = process; State = state; InitialBatches = initialBatches; RemainingBatches = remainingBatches;
        CraftedLevel = craftedLevel; ProgressRatio = progressRatio; SecondsPerBatch = secondsPerBatch;
    }
}

/// <summary>One scoped transfer attempt. Verified amount is null when stack effects or partial failures cannot be attributed safely.</summary>
public sealed class CraftingDeliverySnapshot
{
    public RecipeResourceId? Resource { get; }
    public double RequestedAmount { get; }
    public double? VerifiedAmount { get; }
    public RecipeInventoryKind? Destination { get; }
    public CraftingDeliveryStatus Status { get; }
    public int? ItemLevel { get; }
    public string? Rarity { get; }
    public string Detail { get; }
    public CraftingDeliverySnapshot(RecipeResourceId? resource, double requestedAmount, double? verifiedAmount,
        RecipeInventoryKind? destination, CraftingDeliveryStatus status, string detail, int? itemLevel = null, string? rarity = null)
    {
        if (double.IsNaN(requestedAmount) || double.IsInfinity(requestedAmount)) throw new ArgumentOutOfRangeException(nameof(requestedAmount));
        if (verifiedAmount.HasValue && (double.IsNaN(verifiedAmount.Value) || double.IsInfinity(verifiedAmount.Value) || verifiedAmount < 0)) throw new ArgumentOutOfRangeException(nameof(verifiedAmount));
        if (!Enum.IsDefined(typeof(CraftingDeliveryStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (destination.HasValue && !Enum.IsDefined(typeof(RecipeInventoryKind), destination.Value)) throw new ArgumentOutOfRangeException(nameof(destination));
        if (status == CraftingDeliveryStatus.Verified && (resource == null || !verifiedAmount.HasValue || !destination.HasValue))
            throw new ArgumentException("Verified delivery requires resource, amount and destination evidence.");
        if (status == CraftingDeliveryStatus.NotApplicable) throw new ArgumentException("A transfer attempt needs verified or unresolved status.");
        if (status == CraftingDeliveryStatus.Unresolved && verifiedAmount.HasValue) throw new ArgumentException("Unresolved delivery cannot claim a verified quantity.");
        Resource = resource; RequestedAmount = requestedAmount; VerifiedAmount = verifiedAmount; Destination = destination;
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); ItemLevel = itemLevel; Rarity = rarity;
    }
}

public sealed class CraftingJobListSnapshot
{
    public CraftingJobQueryStatus Status { get; }
    public string Detail { get; }
    public IReadOnlyList<CraftingJobSnapshot> Jobs { get; }
    public CraftingJobListSnapshot(CraftingJobQueryStatus status, string detail, IEnumerable<CraftingJobSnapshot> jobs)
    {
        if (!Enum.IsDefined(typeof(CraftingJobQueryStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail)); Jobs = RecipeValues.Copy(jobs, 4096);
        var ids = new HashSet<CraftingJobHandle>();
        foreach (var job in Jobs) if (!ids.Add(job.Handle)) throw new ArgumentException("Duplicate job handle.", nameof(jobs));
        if (status != CraftingJobQueryStatus.Available && Jobs.Count != 0) throw new ArgumentException("Failed query cannot masquerade as partial success.", nameof(jobs));
    }
}

/// <summary>Immutable scoped fact. BatchObserved can include an exception/unknown delivery; inspect DeliveryStatus and Detail.</summary>
public sealed class CraftingJobEvent
{
    public long Sequence { get; }
    public CraftingJobEventKind Kind { get; }
    public CraftingJobSnapshot Job { get; }
    public IReadOnlyList<CraftingDeliverySnapshot> Deliveries { get; }
    public CraftingDeliveryStatus DeliveryStatus { get; }
    public string Detail { get; }
    public CraftingJobEvent(long sequence, CraftingJobEventKind kind, CraftingJobSnapshot job,
        IEnumerable<CraftingDeliverySnapshot> deliveries, CraftingDeliveryStatus deliveryStatus, string detail)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(typeof(CraftingJobEventKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(CraftingDeliveryStatus), deliveryStatus)) throw new ArgumentOutOfRangeException(nameof(deliveryStatus));
        Sequence = sequence; Kind = kind; Job = job ?? throw new ArgumentNullException(nameof(job));
        Deliveries = RecipeValues.Copy(deliveries, 1024); DeliveryStatus = deliveryStatus; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>Main-thread observations. Query restored jobs; subscribing never replays them as newly queued.</summary>
public interface ICraftingJobService : IServiceStatus
{
    bool IsDispatchingCallbacks { get; }
    CraftingJobListSnapshot Read(RecipeStationHandle station);
    event Action<CraftingJobEvent>? Changed;
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>Customization points do not include mission rewards, ownership reassignment or crew delivery.</summary>
public enum DungeonRewardKind { LootAmount, MasteryExperience }

public interface IDungeonRewardProvider : IDisposable
{
    IDisposable Register(string localId, DungeonRewardKind kind, Func<DungeonRewardContext, DungeonRewardAdjustment> policy);
}

public interface IDungeonRewardService : IServiceStatus
{
    bool IsEvaluating { get; }
    IDungeonRewardProvider AcquireProvider(string pluginId);
}

public sealed class DungeonRewardContext
{
    public BoardingHandle Operation { get; }
    public DungeonRewardKind Kind { get; }
    public string NativeOutcome { get; }
    public bool MissionTokenCapture { get; }
    public double NativeAmount { get; }
    public DungeonRewardContext(BoardingHandle operation, DungeonRewardKind kind, string nativeOutcome, bool missionTokenCapture, double nativeAmount)
    {
        if (!Enum.IsDefined(typeof(DungeonRewardKind), kind) || double.IsNaN(nativeAmount) || double.IsInfinity(nativeAmount) || nativeAmount < 0)
            throw new ArgumentOutOfRangeException(nameof(nativeAmount));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation)); NativeOutcome = nativeOutcome ?? throw new ArgumentNullException(nameof(nativeOutcome));
        Kind = kind; MissionTokenCapture = missionTokenCapture; NativeAmount = nativeAmount;
    }
}

/// <summary>Bounded multiplicative adjustment. It cannot replace native capture or terminal processing.</summary>
public sealed class DungeonRewardAdjustment
{
    public double Multiplier { get; }
    public DungeonRewardAdjustment(double multiplier = 1)
    {
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 0 || multiplier > 10) throw new ArgumentOutOfRangeException(nameof(multiplier));
        Multiplier = multiplier;
    }
}

public interface IDungeonSettlementService : IServiceStatus
{
    bool IsDispatchingCallbacks { get; }
    DungeonSettlementSnapshot? Get(BoardingHandle operation);
    event Action<DungeonSettlementSnapshot>? Changed;
}

/// <summary>Copied outcome facts. Resolved combat and eventual crew return are separate facts.</summary>
public sealed class DungeonSettlementSnapshot
{
    public BoardingHandle Operation { get; }
    public string NativeOutcome { get; }
    public bool CaptureApplied { get; }
    public bool CrewReturnSettled { get; }
    public bool CrewCountsObserved { get; }
    public IReadOnlyDictionary<string, int> Casualties { get; }
    public IReadOnlyDictionary<string, int> PrisonersDelivered { get; }
    public DungeonSettlementSnapshot(BoardingHandle operation, string nativeOutcome, bool captureApplied, bool crewReturnSettled,
        IEnumerable<KeyValuePair<string, int>> casualties, IEnumerable<KeyValuePair<string, int>> prisonersDelivered, bool crewCountsObserved = false)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation)); NativeOutcome = nativeOutcome ?? throw new ArgumentNullException(nameof(nativeOutcome));
        CaptureApplied = captureApplied; CrewReturnSettled = crewReturnSettled;
        CrewCountsObserved = crewCountsObserved;
        Casualties = Copy(casualties); PrisonersDelivered = Copy(prisonersDelivered);
    }
    private static IReadOnlyDictionary<string, int> Copy(IEnumerable<KeyValuePair<string, int>> source)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in source ?? throw new ArgumentNullException(nameof(source)))
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value < 0) throw new ArgumentException("Invalid crew count.");
            result.Add(pair.Key, pair.Value);
        }
        return new ReadOnlyDictionary<string, int>(result);
    }
}

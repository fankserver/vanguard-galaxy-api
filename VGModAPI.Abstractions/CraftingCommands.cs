using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum CraftingCommandKind { Queue, Cancel, Extract, SetSetting }
public enum CraftingProtectionPolicy { NativeConsumption, ProtectFavourites, ProtectFavouritesAndMissionItems }
public enum CraftingSetting { StationAutoRefine, PlayerCargoDelivery, PlayerAutoSell }
public enum CraftingCommandStatus
{
    Succeeded, Rejected, InvalidRequest, IntegrationUnavailable, SessionUnavailable, StaleHandle, Busy,
    Unsupported, QueueFull, InsufficientCredits, MissingInputs, ProtectedInputs, StorageUnavailable,
    RequestConflict, RequestLimitExceeded, Uncertain
}

/// <summary>One explicit intent. Reuse its ID only to retrieve that same intent's result, never to retry an uncertain mutation.</summary>
public sealed class CraftingCommandRequest
{
    public const int MaximumBatches = 10000;
    public string PluginId { get; }
    public Guid RequestId { get; }
    public Guid SessionId { get; }
    public CraftingCommandKind Kind { get; }
    public RecipeStationHandle? Station { get; }
    public RecipeId? Recipe { get; }
    public CraftingJobHandle? Job { get; }
    public RecipeResourceId? Material { get; }
    public int Count { get; }
    public CraftingProtectionPolicy Protection { get; }
    public CraftingSetting? Setting { get; }
    public bool SettingValue { get; }
    private CraftingCommandRequest(string pluginId, Guid requestId, Guid sessionId, CraftingCommandKind kind,
        RecipeStationHandle? station = null, RecipeId? recipe = null, CraftingJobHandle? job = null, RecipeResourceId? material = null,
        int count = 0, CraftingProtectionPolicy protection = CraftingProtectionPolicy.NativeConsumption, CraftingSetting? setting = null, bool value = false)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || pluginId.Length > 512) throw new ArgumentException("Bounded plugin identity required.", nameof(pluginId));
        if (requestId == Guid.Empty || sessionId == Guid.Empty) throw new ArgumentException("Nonempty request and session identities required.");
        if (station != null && station.SessionId != sessionId) throw new ArgumentException("Station belongs to another session.");
        PluginId = pluginId; RequestId = requestId; SessionId = sessionId; Kind = kind; Station = station; Recipe = recipe;
        Job = job; Material = material; Count = count; Protection = protection; Setting = setting; SettingValue = value;
    }
    public static CraftingCommandRequest Queue(string pluginId, Guid requestId, RecipeStationHandle station, RecipeId recipe, int batches, CraftingProtectionPolicy protection)
    {
        if (station == null || recipe == null) throw new ArgumentNullException(station == null ? nameof(station) : nameof(recipe));
        ValidateCount(batches);
        if (!Enum.IsDefined(typeof(CraftingProtectionPolicy), protection)) throw new ArgumentOutOfRangeException(nameof(protection));
        return new(pluginId, requestId, station.SessionId, CraftingCommandKind.Queue, station, recipe, count: batches, protection: protection);
    }
    public static CraftingCommandRequest Cancel(string pluginId, Guid requestId, CraftingJobHandle job)
    {
        if (job == null) throw new ArgumentNullException(nameof(job));
        return new(pluginId, requestId, job.Station.SessionId, CraftingCommandKind.Cancel, job.Station, job: job);
    }
    public static CraftingCommandRequest Extract(string pluginId, Guid requestId, RecipeStationHandle station, RecipeResourceId material, int count)
    {
        if (station == null || material == null) throw new ArgumentNullException(station == null ? nameof(station) : nameof(material));
        ValidateCount(count);
        if (material.Kind != RecipeResourceKind.RefinedMaterial) throw new ArgumentException("Refined material required.", nameof(material));
        return new(pluginId, requestId, station.SessionId, CraftingCommandKind.Extract, station, material: material, count: count);
    }
    public static CraftingCommandRequest Configure(string pluginId, Guid requestId, Guid sessionId, CraftingSetting setting, bool value, RecipeStationHandle? station = null)
    {
        if (!Enum.IsDefined(typeof(CraftingSetting), setting)) throw new ArgumentOutOfRangeException(nameof(setting));
        if (setting == CraftingSetting.StationAutoRefine && station == null) throw new ArgumentNullException(nameof(station));
        if (setting != CraftingSetting.StationAutoRefine && station != null) throw new ArgumentException("Player settings do not belong to a station.", nameof(station));
        return new(pluginId, requestId, sessionId, CraftingCommandKind.SetSetting, station, setting: setting, value: value);
    }
    private static void ValidateCount(int value) { if (value < 1 || value > MaximumBatches) throw new ArgumentOutOfRangeException(nameof(value)); }
}

/// <summary>Command outcome, not a guarantee of atomicity. Native invocation may also initialize native pricing/preview caches.</summary>
public sealed class CraftingCommandResult
{
    public Guid RequestId { get; }
    public CraftingCommandStatus Status { get; }
    public string Detail { get; }
    /// <summary>Native mutation or price initialization may have run; false is not a success signal.</summary>
    public bool MutationMayHaveRun { get; }
    public bool IsReplay { get; }
    /// <summary>Observed player credit change during the call. An uncertain outcome does not attribute it solely to this request.</summary>
    public long? CreditDelta { get; }
    public IReadOnlyList<CraftingJobHandle> Jobs { get; }
    public IReadOnlyList<CraftingDeliverySnapshot> Deliveries { get; }
    public CraftingCommandResult(Guid requestId, CraftingCommandStatus status, string detail, bool mutationMayHaveRun = false, bool isReplay = false,
        long? creditDelta = null, IEnumerable<CraftingJobHandle>? jobs = null, IEnumerable<CraftingDeliverySnapshot>? deliveries = null)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("Request identity required.", nameof(requestId));
        if (!Enum.IsDefined(typeof(CraftingCommandStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        RequestId = requestId; Status = status; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        MutationMayHaveRun = mutationMayHaveRun; IsReplay = isReplay; CreditDelta = creditDelta;
        Jobs = RecipeValues.Copy(jobs ?? Array.Empty<CraftingJobHandle>(), 256);
        Deliveries = RecipeValues.Copy(deliveries ?? Array.Empty<CraftingDeliverySnapshot>(), 1024);
    }
}

public sealed class CraftingSettingsSnapshot
{
    public Guid SessionId { get; }
    public bool Available { get; }
    public bool? StationAutoRefine { get; }
    public bool? PlayerCargoDelivery { get; }
    public bool? EffectiveAutoSell { get; }
    public bool? StoredAutoSellPreference { get; }
    public string Detail { get; }
    public CraftingSettingsSnapshot(Guid sessionId, bool available, bool? stationAutoRefine, bool? playerCargoDelivery,
        bool? effectiveAutoSell, bool? storedAutoSellPreference, string detail)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session identity required.", nameof(sessionId));
        SessionId = sessionId; Available = available; StationAutoRefine = stationAutoRefine; PlayerCargoDelivery = playerCargoDelivery;
        EffectiveAutoSell = effectiveAutoSell; StoredAutoSellPreference = storedAutoSellPreference; Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

public interface ICraftingCommandService : IServiceStatus
{
    CraftingCommandResult Execute(CraftingCommandRequest request);
    CraftingSettingsSnapshot ReadSettings(Guid sessionId, RecipeStationHandle? station = null);
}

using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class CraftingCommandServiceTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly Jobs _jobs = new();
    private readonly Backend _backend = new();
    private readonly CraftingCommandService _service;
    private readonly RecipeStationHandle _station;
    public CraftingCommandServiceTests()
    {
        var token = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(token); _hub.GameplayInitialized(token);
        _station = new(_hub.CurrentSession!.Id, Guid.NewGuid(), "Station");
        _service = new(_hub, _jobs, _backend, _ => { }); _service.SetAvailable(true);
    }
    private CraftingCommandRequest Request(Guid? id = null, int count = 1) => CraftingCommandRequest.Queue("test", id ?? Guid.NewGuid(), _station,
        new("vanilla", "forge/test"), count, CraftingProtectionPolicy.ProtectFavouritesAndMissionItems);
    [Fact]
    public void FaultDuringExecutionClosesHealthAndReportsUncertainEffects()
    {
        _backend.Action = _ => _service.RecordFault(new InvalidOperationException("Native fault."));
        var result = _service.Execute(Request());
        Assert.Equal(CraftingCommandStatus.Uncertain, result.Status);
        Assert.True(result.MutationMayHaveRun);
        Assert.Equal(ServiceUnavailableReason.ObserverFault, _service.Availability.Reason);
        Assert.Equal(CraftingCommandStatus.IntegrationUnavailable, _service.Execute(Request()).Status);
        Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void MissingCommandBackendIsAnUnavailableTypedService()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("crafting-commands", false, "Commands disabled.", ServiceUnavailableReason.Disabled);
        using var service = new CraftingCommandService(hub, _jobs, null, _ => { });
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Equal(CraftingCommandStatus.IntegrationUnavailable, service.Execute(Request()).Status);
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.ICraftingCommands"));
        Assert.Null(typeof(ModApi).GetProperty("CraftingCommands"));
    }
    [Fact]
    public void DuplicateIntentReturnsReceiptWithoutExecutingAgain()
    {
        var request = Request(); var first = _service.Execute(request); var second = _service.Execute(request);
        Assert.Equal(CraftingCommandStatus.Succeeded, first.Status); Assert.True(second.IsReplay); Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void ChangedIntentCannotReuseRequestId()
    {
        var request = Request(); _service.Execute(request);
        Assert.Equal(CraftingCommandStatus.RequestConflict, _service.Execute(Request(request.RequestId, 2)).Status); Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void UncertainExceptionIsCachedAndNeverBlindlyRetried()
    {
        _backend.Action = _ => throw new InvalidOperationException(); var request = Request();
        Assert.Equal(CraftingCommandStatus.Uncertain, _service.Execute(request).Status);
        Assert.True(_service.Execute(request).IsReplay); Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void SerializationAndObserverDispatchRefuseFreshMutations()
    {
        var request = Request(); _service.BeginSerialization();
        Assert.Equal(CraftingCommandStatus.Busy, _service.Execute(request).Status); _service.EndSerialization();
        _jobs.IsDispatchingCallbacks = true; Assert.Equal(CraftingCommandStatus.Busy, _service.Execute(request).Status);
        _jobs.IsDispatchingCallbacks = false; Assert.Equal(CraftingCommandStatus.Succeeded, _service.Execute(request).Status);
        Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void ReentrantCommandCannotExecuteNativeBackend()
    {
        CraftingCommandResult? nested = null;
        _backend.Action = _ => nested = _service.Execute(Request());
        _service.Execute(Request()); Assert.Equal(CraftingCommandStatus.Busy, nested!.Status); Assert.Equal(1, _backend.Calls);
    }
    [Fact]
    public void SessionReplacementDuringExecutionMakesOutcomeUncertain()
    {
        _backend.Action = _ => { var token = _hub.Begin(SessionOrigin.SaveLoad, "replacement"); _hub.PlayerReady(token); _hub.GameplayInitialized(token); };
        var request = Request(); Assert.Equal(CraftingCommandStatus.Uncertain, _service.Execute(request).Status);
        Assert.Equal(CraftingCommandStatus.StaleHandle, _service.Execute(request).Status);
    }
    [Fact]
    public void BoundedCountsAndSettingScopesAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(count: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(count: CraftingCommandRequest.MaximumBatches + 1));
        Assert.Throws<ArgumentException>(() => CraftingCommandRequest.Configure("test", Guid.NewGuid(), _station.SessionId,
            CraftingSetting.PlayerAutoSell, true, _station));
        Assert.Throws<ArgumentNullException>(() => CraftingCommandRequest.Configure("test", Guid.NewGuid(), _station.SessionId, CraftingSetting.StationAutoRefine, true));
    }
    public void Dispose() { _service.Dispose(); _hub.Dispose(); }
    private sealed class Backend : ICraftingCommandBackend
    {
        internal int Calls;
        internal Action<CraftingCommandRequest>? Action;
        public CraftingCommandResult Execute(CraftingCommandRequest request)
        { Calls++; Action?.Invoke(request); return new(request.RequestId, CraftingCommandStatus.Succeeded, "Test", true); }
        public CraftingSettingsSnapshot ReadSettings(Guid session, RecipeStationHandle? station) => new(session, true, null, true, false, false, "Read");
    }
    private sealed class Jobs : FakeServiceStatus, ICraftingJobService
    {
        public bool IsDispatchingCallbacks { get; set; }
        public CraftingJobListSnapshot Read(RecipeStationHandle station) => new(CraftingJobQueryStatus.Available, "Read", Array.Empty<CraftingJobSnapshot>());
        public event Action<CraftingJobEvent>? Changed { add { } remove { } }
    }
}

using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCommandServiceTests
{
    private sealed class Backend : IBoardingCommandBackend
    {
        internal int Calls;
        internal Action? Pause, ExecuteAction;
        public BoardingCommandResult ValidateControl(BoardingHandle target) => BoardingCommandService.Result(BoardingCommandStatus.Admitted);
        public void PauseAutonomous(BoardingHandle target) => Pause?.Invoke();
        public BoardingCommandResult Execute(BoardingHandle target, BoardingCommandKind command, BoardingCrewManifest? crew, BoardingCommandOptions? options, bool allowFactionConsequences)
        { Calls++; ExecuteAction?.Invoke(); return BoardingCommandService.Result(BoardingCommandStatus.Admitted); }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HealthLossDuringNativeWorkReportsUncertainAndRevokesAdmission(bool duringPause)
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new BoardingService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session);
        var target = new BoardingHandle(session, Guid.NewGuid());
        events.Observe(BoardingEventKind.TargetAvailable, new(target, 1, BoardingEncounterKind.Ship, "ship", null, null, BoardingAvailability.Available, null));
        hub.SetCapability("boarding-commands", true, "Test bindings.");
        Action fail = () => hub.SetCapability("boarding-commands", false, "Fault.", ServiceUnavailableReason.ObserverFault);
        var backend = new Backend { Pause = duringPause ? fail : null, ExecuteAction = fail };
        using var commands = new BoardingCommandService(hub, events, backend, () => false);
        var result = commands.AcquireControl("mod", target, out var controller);
        if (duringPause) Assert.Null(controller);
        else { Assert.True(result.Admitted); result = controller!.Resume(); Assert.False(controller.IsActive); }
        Assert.Equal(BoardingCommandStatus.Uncertain, result.Status);
        Assert.Equal(BoardingCommandStatus.IntegrationUnavailable, commands.AcquireControl("late", target, out _).Status);
        commands.Dispose();
        Assert.Equal(ServiceUnavailableReason.ObserverFault, commands.Availability.Reason);
    }
    [Fact]
    public void MissingBackendRemainsUnavailableWithoutNativeAdmission()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("boarding-commands", false, "Disabled.", ServiceUnavailableReason.Disabled);
        using var engine = new BoardingCommandService(hub, null, null, () => false);
        IBoardingCommandService service = engine;
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Equal(BoardingCommandStatus.IntegrationUnavailable, service.AcquireControl("mod", new(Guid.NewGuid(), Guid.NewGuid()), out var controller).Status);
        Assert.Null(controller);
        Assert.Null(typeof(ModApi).GetProperty("BoardingCommands"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IBoardingCommands"));
    }
    [Fact]
    public void ExclusiveControlIsInstanceScopedAndManualTakeoverRevokesIt()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new BoardingService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var target = new BoardingHandle(session, Guid.NewGuid());
        events.Observe(BoardingEventKind.TargetAvailable, new(target, 1, BoardingEncounterKind.Ship, "ship", null, null, BoardingAvailability.Available, null));
        var backend = new Backend(); hub.SetCapability("boarding-commands", true, "Test bindings."); using var commands = new BoardingCommandService(hub, events, backend, () => false);
        Assert.True(commands.AcquireControl("a", target, out var first).Admitted);
        Assert.Equal(BoardingCommandStatus.ControlConflict, commands.AcquireControl("a", target, out _).Status);
        commands.ManualTakeover(target); Assert.False(first!.IsActive);
        Assert.True(commands.AcquireControl("a", target, out var second).Admitted);
        first.Dispose(); Assert.True(second!.IsActive);
        Assert.Equal(BoardingCommandStatus.ControlConflict, first.Resume().Status);
        Assert.True(second.Resume().Admitted); Assert.Equal(1, backend.Calls);
    }
    [Fact]
    public void NativeExceptionsPropagateAndBusyGuardIsReleased()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new BoardingService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var target = new BoardingHandle(session, Guid.NewGuid());
        events.Observe(BoardingEventKind.TargetAvailable, new(target, 1, BoardingEncounterKind.Ship, "ship", null, null, BoardingAvailability.Available, null));
        var backend = new Backend(); hub.SetCapability("boarding-commands", true, "Test bindings."); using var commands = new BoardingCommandService(hub, events, backend, () => false);
        commands.AcquireControl("a", target, out var controller);
        var error = new InvalidOperationException("native"); backend.ExecuteAction = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => controller!.Resume()));
        backend.ExecuteAction = () => Assert.Equal(BoardingCommandStatus.Busy, controller!.Resume().Status);
        Assert.True(controller!.Resume().Admitted);
        hub.Invalidate("end"); Assert.False(controller.IsActive);
        Assert.False(controller.Resume().Admitted);
    }
    [Fact]
    public void SessionReplacementDuringArbitrationCannotGrantControl()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new BoardingService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var target = new BoardingHandle(session, Guid.NewGuid());
        events.Observe(BoardingEventKind.TargetAvailable, new(target, 1, BoardingEncounterKind.Ship, "ship", null, null, BoardingAvailability.Available, null));
        var backend = new Backend { Pause = () => hub.Invalidate("replaced") };
        hub.SetCapability("boarding-commands", true, "Test bindings."); using var commands = new BoardingCommandService(hub, events, backend, () => false);
        Assert.Equal(BoardingCommandStatus.StaleHandle, commands.AcquireControl("a", target, out var controller).Status);
        Assert.Null(controller);
    }
}

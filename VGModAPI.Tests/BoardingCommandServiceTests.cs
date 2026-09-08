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
    [Fact]
    public void ExclusiveControlIsInstanceScopedAndManualTakeoverRevokesIt()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new BoardingService(hub, (_, _) => { });
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); hub.GameplayInitialized(session);
        var target = new BoardingHandle(session, Guid.NewGuid());
        events.Observe(BoardingEventKind.TargetAvailable, new(target, 1, BoardingEncounterKind.Ship, "ship", null, null, BoardingAvailability.Available, null));
        var backend = new Backend(); using var commands = new BoardingCommandService(hub, events, backend, () => false);
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
        var backend = new Backend(); using var commands = new BoardingCommandService(hub, events, backend, () => false);
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
        using var commands = new BoardingCommandService(hub, events, backend, () => false);
        Assert.Equal(BoardingCommandStatus.StaleHandle, commands.AcquireControl("a", target, out var controller).Status);
        Assert.Null(controller);
    }
}

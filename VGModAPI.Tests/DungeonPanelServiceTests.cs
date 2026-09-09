using System;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonPanelServiceTests
{
    private sealed class Source : IDungeonPanelSource
    {
        internal DungeonPanelSnapshot? Snapshot;
        internal Func<BoardingHandle, DungeonPanelOpenStatus>? OpenCallback;
        public DungeonPanelCapabilities Capabilities { get; set; } = new(true, true, true);
        internal int Reads;
        public DungeonPanelSnapshot? Read() { Reads++; return Snapshot; }
        public DungeonPanelOpenStatus Open(BoardingHandle target) => OpenCallback?.Invoke(target) ?? (Snapshot?.Target.Handle.Equals(target) == true ? DungeonPanelOpenStatus.Opened : DungeonPanelOpenStatus.StaleTarget);
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly LifecycleHub Hub = new((_, _) => { }); internal readonly Source Source = new();
        internal readonly DungeonPanelService Service; internal int Faults;
        internal Fixture()
        {
            var session = Hub.Begin(SessionOrigin.NewGame, null); Hub.PlayerReady(session); Hub.GameplayInitialized(session);
            var target = new BoardingTargetSnapshot(new(session, Guid.NewGuid()), 1, BoardingEncounterKind.Installation, "Site", null, null, BoardingAvailability.Available, null);
            Hub.SetCapability("dungeon-panel-opening", true, "Test bindings.");
            Source.Snapshot = new(Guid.NewGuid(), 1, target, null); Service = new(Hub, Source, (_, _) => Faults++);
        }
        public void Dispose() { Service.Dispose(); Hub.Dispose(); }
    }
    [Fact]
    public void HealthLossDuringNavigationIsUncertainAndStopsFurtherReads()
    {
        using var f = new Fixture(); IDungeonPanelService service = f.Service;
        f.Source.OpenCallback = _ =>
        { f.Hub.SetCapability("dungeon-panel-opening", false, "Fault.", ServiceUnavailableReason.ObserverFault); return DungeonPanelOpenStatus.Opened; };
        Assert.Equal(DungeonPanelOpenStatus.Uncertain, service.Open(f.Source.Snapshot!.Target.Handle));
        var reads = f.Source.Reads;
        Assert.Null(service.Current); Assert.Equal(reads, f.Source.Reads);
        Assert.False(service.Capabilities.Opening);
        f.Service.Dispose(); Assert.Equal(ServiceUnavailableReason.ObserverFault, service.Availability.Reason);
        Assert.Null(typeof(ModApi).GetProperty("DungeonPanel"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IDungeonPanelApi"));
    }
    [Fact]
    public void MissingSourceRetainsSpecificUnavailableDiagnosis()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("dungeon-panel-opening", false, "Disabled.", ServiceUnavailableReason.Disabled);
        using var service = new DungeonPanelService(hub, null, (_, _) => { });
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Null(service.Current); Assert.False(service.Capabilities.ContextualActions);
        Assert.Equal(DungeonPanelOpenStatus.Unavailable, service.Open(new(Guid.NewGuid(), Guid.NewGuid())));
    }
    [Fact]
    public void NativeNavigationCannotReenterOrActivateControlsAndReleasesGateOnFailure()
    {
        using var f = new Fixture();
        f.Service.RegisterAction("mod", "action", _ => new("Act"), _ => throw new InvalidOperationException("Unexpected activation"));
        var row = Assert.Single(f.Service.Render());
        f.Source.OpenCallback = target =>
        {
            Assert.Equal(DungeonPanelOpenStatus.Busy, f.Service.Open(target));
            Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision));
            Assert.Empty(f.Service.Render()); throw new InvalidOperationException("Native navigation failure");
        };
        Assert.Throws<InvalidOperationException>(() => f.Service.Open(row.Snapshot.Target.Handle));
        f.Source.OpenCallback = null; Assert.Equal(DungeonPanelOpenStatus.Opened, f.Service.Open(row.Snapshot.Target.Handle));
    }
    [Fact]
    public void ActivationRechecksPresentationAndRejectsOldViewsAndDisposedLeases()
    {
        using var f = new Fixture(); var enabled = true; var calls = 0;
        var lease = f.Service.RegisterAction("mod", "action", _ => new("Act", enabled: enabled), _ => calls++);
        var row = Assert.Single(f.Service.Render()); enabled = false;
        Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision)); enabled = true;
        f.Source.Snapshot = new(Guid.NewGuid(), 1, row.Snapshot.Target, null);
        Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision));
        row = Assert.Single(f.Service.Render()); Assert.True(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision)); Assert.Equal(1, calls);
        lease.Dispose(); Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision)); Assert.Empty(f.Service.Render());
    }
    [Theory]
    [InlineData("closed")]
    [InlineData("session")]
    [InlineData("revision")]
    public void PresentationMutationCannotAuthorizeStaleAction(string change)
    {
        using var f = new Fixture(); var mutate = false; var calls = 0;
        f.Service.RegisterAction("mod", "action", snapshot =>
        {
            if (mutate)
            {
                if (change == "closed") f.Source.Snapshot = null;
                else if (change == "session") f.Hub.Begin(SessionOrigin.NewGame, null);
                else f.Source.Snapshot = new(snapshot.ViewId, snapshot.Revision + 1, snapshot.Target, null);
            }
            return new("Act");
        }, _ => calls++);
        var row = Assert.Single(f.Service.Render()); mutate = true;
        Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision)); Assert.Equal(0, calls);
    }
    [Fact]
    public void ContributorsAreOrderedIsolatedAndIndependentlyGated()
    {
        using var f = new Fixture();
        f.Service.RegisterSection("broken", "status", _ => throw new InvalidOperationException());
        f.Service.RegisterSection("z", "status", _ => new("Z", new string('x', 4096)));
        f.Service.RegisterAction("a", "act", _ => new(new string('a', 128)), _ => { });
        var rows = f.Service.Render(); Assert.Equal(2, rows.Count); Assert.NotNull(rows[0].Action); Assert.NotNull(rows[1].Section); Assert.Equal(1, f.Faults);
        f.Source.Capabilities = new(false, true, false); Assert.Single(f.Service.Render());
        Assert.Equal(DungeonPanelOpenStatus.Unavailable, f.Service.Open(f.Source.Snapshot!.Target.Handle));
        Assert.Throws<InvalidOperationException>(() => f.Service.RegisterSection("z", "status", _ => null));
        Assert.Throws<ArgumentException>(() => new DungeonPanelSection("title", new string('x', 4097)));
    }
    [Fact]
    public void RecursiveActionIsRefusedAndCallbackFailureDoesNotEscape()
    {
        using var f = new Fixture(); DungeonPanelService.Row? row = null;
        f.Service.RegisterAction("mod", "action", _ => new("Act"), _ =>
        {
            Assert.False(f.Service.Activate(row!.Registration, row.Snapshot.ViewId, row.Snapshot.Revision));
            throw new InvalidOperationException("consumer failure");
        });
        row = Assert.Single(f.Service.Render()); Assert.False(f.Service.Activate(row.Registration, row.Snapshot.ViewId, row.Snapshot.Revision)); Assert.Equal(1, f.Faults);
    }
}

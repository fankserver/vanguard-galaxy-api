using System;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class HudServiceTests
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly HudService _service;
    private readonly Guid _session, _surface = Guid.NewGuid();
    public HudServiceTests()
    {
        _session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(_session); _hub.GameplayInitialized(_session);
        _service = new(_hub, (_, _) => { }); _service.SetAvailable(true); _service.SetSurface(_surface, _session);
    }
    [Fact]
    public void TypedHealthIsIndependentOfSurfaceVisibility()
    {
        IHudService service = _service;
        _service.SetSurface(null, null);
        Assert.True(service.Availability.IsAvailable);
        Assert.False(service.Visible);
        var notifications = 0;
        Action<ServiceAvailability> handler = _ => notifications++;
        service.AvailabilityChanged += handler;
        _service.SetAvailable(false);
        Assert.False(service.Availability.IsAvailable);
        Assert.Equal(1, notifications);
        service.AvailabilityChanged -= handler;
        _service.SetAvailable(true);
        Assert.Equal(1, notifications);
        Assert.False(service.Visible);
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IModHud"));
        Assert.Null(typeof(ModApi).GetProperty("Hud"));
    }
    [Fact]
    public void DisposalClosesInputBeforeNotifyingAndIsReentrantSafe()
    {
        IHudService service = _service;
        var calls = 0;
        var visibleDuringStop = true;
        using var registration = service.Register("one", "button", _ => calls++);
        registration.Update(new("One"), null);
        var entry = _service.Entries.Single();
        service.AvailabilityChanged += _ => { visibleDuringStop = service.Visible; _service.Dispose(); };
        _service.Dispose();
        Assert.False(visibleDuringStop);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
        Assert.Empty(_service.Entries);
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Button));
        Assert.Equal(0, calls);
        Assert.Throws<ObjectDisposedException>(() => service.Register("two", "button", _ => { }));
    }
    [Fact]
    public void NamespacesOrderingAndDisposalPreventCrossRegistrationClicks()
    {
        var calls = 0; var first = _service.Register("one", "button", _ => calls++);
        first.Update(new("One"), null); var old = _service.Entries.Single();
        using var second = _service.Register("two", "button", _ => { }, -1);
        Assert.Equal("two", _service.Entries.First().Plugin);
        Assert.Throws<InvalidOperationException>(() => _service.Register("one", "button", _ => { }));
        Assert.True(_service.Invoke(old.Token, _surface, old.Revision, HudInteractionKind.Button));
        first.Dispose(); using var replacement = _service.Register("one", "button", _ => calls++);
        Assert.False(_service.Invoke(old.Token, _surface, old.Revision, HudInteractionKind.Button)); Assert.Equal(1, calls);
    }
    [Fact]
    public void LiveVisibilityIsRecheckedBeforeInputWithoutWaitingForNextTick()
    {
        using var registration = _service.Register("one", "button", _ => { }); registration.Update(new("One"), null);
        var entry = _service.Entries.Single(); _service.SurfaceLive = () => false;
        Assert.False(_service.Visible); Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Button));
    }
    [Fact]
    public void ContentRevisionAndSurfaceReplacementInvalidateRenderedInput()
    {
        using var registration = _service.Register("one", "button", _ => { }); registration.Update(new("A"), null);
        var entry = _service.Entries.Single(); var revision = entry.Revision;
        registration.Update(new("B"), null);
        Assert.False(_service.Invoke(entry.Token, _surface, revision, HudInteractionKind.Button));
        _service.SetSurface(Guid.NewGuid(), _session);
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Button));
    }
    [Fact]
    public void SessionLossHidesSurfaceWithoutDiscardingRegistrations()
    {
        using var registration = _service.Register("one", "button", _ => { });
        _hub.Begin(SessionOrigin.NewGame, null); Assert.False(_service.Visible); Assert.Single(_service.Entries);
    }
    [Fact]
    public void HiddenAndDisabledAndUnknownRowInputsAreRejected()
    {
        using var registration = _service.Register("one", "panel", _ => { });
        registration.Update(new("Disabled", enabled: false), new("Title", new[] { new HudRow("row", "Row", clickable: false) }));
        var entry = _service.Entries.Single();
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Button));
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Row, "row"));
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Row, "missing"));
        _service.SetSurface(null, null); Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.ClosePanel));
    }
    [Fact]
    public void PanelLimitsRejectUpdatesWithoutReplacingExistingContent()
    {
        for (var i = 0; i < 4; i++) _service.Register("one", i.ToString(), _ => { }).Update(null, new("Panel", Array.Empty<HudRow>()));
        using var last = _service.Register("one", "last", _ => { });
        Assert.Throws<InvalidOperationException>(() => last.Update(null, new("Overflow", Array.Empty<HudRow>())));
        Assert.Null(_service.Entries.Single(entry => entry.Local == "last").Panel);
        _service.Dispose(); Assert.Empty(_service.Entries); Assert.False(_service.Visible);
    }
    [Fact]
    public void CallbackFailureIsIsolatedAndReentrantInvocationIsRefused()
    {
        HudService.Entry? entry = null;
        using var registration = _service.Register("one", "button", _ =>
        { Assert.False(_service.Invoke(entry!.Token, _surface, entry.Revision, HudInteractionKind.Button)); throw new InvalidOperationException(); });
        registration.Update(new("One"), null); entry = _service.Entries.Single();
        Assert.False(_service.Invoke(entry.Token, _surface, entry.Revision, HudInteractionKind.Button));
        Assert.True(_service.Visible);
    }
}

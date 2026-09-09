using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ForgeUiServiceTests
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly Source _source = new();
    private readonly ForgeUiService _service;
    private readonly Guid _session;
    private readonly RecipeId _parent = new("vanilla", "forge/root");
    private readonly RecipeId _variant = new("vanilla", "forge/variant");
    public ForgeUiServiceTests()
    {
        _session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(_session); _hub.GameplayInitialized(_session);
        _service = new(_hub, _source, (_, _) => { }); _service.SetAvailable(true);
    }
    private ForgeSelectionSnapshot Frame(int count = 2, ForgeViewHandle? view = null) => new(view ?? new(_session, Guid.NewGuid()),
        new(_session, _session, "Station"), _parent, _variant, new[] { _parent, _variant }, count, 0, new("Variant", true));
    [Fact]
    public void TypedSelectionHandlersAreScopedIsolatedAndRemovable()
    {
        IForgeUiService service = _service;
        var observed = new List<bool>();
        var navigation = new List<ForgeNavigationStatus>();
        Action<ForgeSelectionChange> handler = _ => throw new InvalidOperationException("Expected subscriber failure.");
        handler += _ => { observed.Add(_hub.IsDispatchingCallbacks); navigation.Add(service.Open(_variant)); };
        service.Changed += handler;
        _source.Value = Frame(); _service.Refresh();
        Assert.True(Assert.Single(observed));
        Assert.Equal(ForgeNavigationStatus.Busy, Assert.Single(navigation));
        service.Changed -= handler;
        _source.Value = Frame(); _service.Refresh();
        Assert.Single(observed);
        Assert.Null(typeof(ModApi).GetProperty("ForgeUi"));
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IForgeUi"));
    }
    [Fact]
    public void DisposalNotifiesClosureAndClosesNavigationBeforeHealthCallbacks()
    {
        IForgeUiService service = _service;
        _source.Value = Frame(); Assert.NotNull(service.Current);
        var changes = new List<ForgeSelectionChange>();
        var navigation = new List<ForgeNavigationStatus>();
        service.Changed += changes.Add;
        service.AvailabilityChanged += _ => { navigation.Add(service.Open(_variant)); _service.Dispose(); };
        _service.Dispose();
        Assert.Null(Assert.Single(changes).Current);
        Assert.Equal(ForgeNavigationStatus.Unavailable, Assert.Single(navigation));
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
        Assert.Null(service.Current);
    }
    [Fact]
    public void MissingSourcePreservesUnavailableDiagnosis()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("forge-ui", false, "Disabled by configuration.", ServiceUnavailableReason.Disabled);
        using var service = new ForgeUiService(hub, null, (_, _) => { });
        Assert.Equal(ServiceUnavailableReason.Disabled, service.Availability.Reason);
        Assert.Null(service.Current);
        Assert.Equal(ForgeNavigationStatus.Unavailable, service.Open(_variant));
    }
    [Fact]
    public void OpenCloseReopenKeepsRegistrationsButInvalidatesOldClicks()
    {
        var calls = 0; using var registration = _service.RegisterAction("a", "pin", new("Pin"), _ => calls++);
        _source.Value = Frame(); var old = _service.Current!; var token = _service.Actions.Single().Token;
        Assert.True(_service.Invoke(token, old.View, old.Revision));
        _source.Value = null; Assert.Null(_service.Current);
        _source.Value = Frame(); var reopened = _service.Current!;
        Assert.NotEqual(old.View, reopened.View); Assert.False(_service.Invoke(token, old.View, old.Revision));
        Assert.True(_service.Invoke(token, reopened.View, reopened.Revision)); Assert.Equal(2, calls);
    }
    [Fact]
    public void CountChangesInvalidateRenderedRevisionAndNotifyOnce()
    {
        var changes = new List<ForgeSelectionChange>(); _service.Subscribe("a", changes.Add);
        _source.Value = Frame(); var before = _service.Current!;
        _source.Value = Frame(4, before.View); var after = _service.Current!;
        _ = _service.Current;
        Assert.Equal(2, changes.Count); Assert.Equal(4, after.Batches); Assert.True(after.Revision > before.Revision);
        using var action = _service.RegisterAction("a", "pin", new("Pin"), _ => { });
        Assert.False(_service.Invoke(_service.Actions.Single().Token, before.View, before.Revision));
    }
    [Fact]
    public void ProvidersCoexistAndDisposedTokensCannotInvokeReplacement()
    {
        _source.Value = Frame(); var selection = _service.Current!;
        var a = _service.RegisterAction("a", "pin", new("Pin"), _ => { });
        var old = _service.Actions.Single().Token;
        using var b = _service.RegisterAction("b", "pin", new("Pin"), _ => { }, -1);
        Assert.Equal("b", _service.Actions.First().Plugin);
        Assert.Throws<InvalidOperationException>(() => _service.RegisterAction("a", "pin", new("Duplicate"), _ => { }));
        a.Dispose(); using var replacement = _service.RegisterAction("a", "pin", new("New"), _ => { });
        Assert.False(_service.Invoke(old, selection.View, selection.Revision));
    }
    [Fact]
    public void SessionReplacementClearsSelectionNotProviderRegistration()
    {
        using var action = _service.RegisterAction("a", "pin", new("Pin"), _ => { });
        _source.Value = Frame(); Assert.NotNull(_service.Current);
        _hub.Begin(SessionOrigin.NewGame, null);
        Assert.Null(_service.Current); Assert.Single(_service.Actions); Assert.Null(_source.Value);
    }
    [Fact]
    public void SubscribersAreIsolatedAndCanDisposeOthers()
    {
        var calls = 0; IDisposable? later = null;
        _service.Subscribe("first", _ => { later!.Dispose(); throw new InvalidOperationException(); });
        later = _service.Subscribe("later", _ => calls++);
        _service.Subscribe("last", _ => calls++);
        _source.Value = Frame(); _service.Refresh(); Assert.Equal(1, calls);
    }
    [Fact]
    public void NavigationChecksOutcomeAndRefusesSnapshotCallbackRecursion()
    {
        _service.Subscribe("a", _ => Assert.Equal(ForgeNavigationStatus.Busy, _service.Open(_variant)));
        _source.OnOpen = _ => { _source.Value = Frame(); return ForgeNavigationStatus.Selected; };
        Assert.Equal(ForgeNavigationStatus.Selected, _service.Open(_variant));
        Assert.Equal(1, _source.Opens);
        _source.OnOpen = _ => ForgeNavigationStatus.RecipeUnavailable;
        Assert.Equal(ForgeNavigationStatus.RecipeUnavailable, _service.Open(new("vanilla", "missing")));
    }
    [Fact]
    public void FailedReadDisablesIntegrationAndDisposalReleasesRegistrations()
    {
        using var action = _service.RegisterAction("a", "pin", new("Pin"), _ => { });
        _source.Fail = true; Assert.Null(_service.Current);
        Assert.Equal(ForgeNavigationStatus.Unavailable, _service.Open(_variant));
        _service.Dispose(); Assert.Empty(_service.Actions);
        Assert.Throws<ObjectDisposedException>(() => action.Update(new("No")));
    }
    private sealed class Source : IForgeUiSource
    {
        internal ForgeSelectionSnapshot? Value;
        internal Func<RecipeId, ForgeNavigationStatus>? OnOpen;
        internal int Opens;
        internal bool Fail;
        public ForgeSelectionSnapshot? ReadUi(Guid session) => Fail ? throw new InvalidOperationException() : Value;
        public ForgeNavigationStatus OpenUi(Guid session, RecipeId recipe) { Opens++; return OnOpen?.Invoke(recipe) ?? ForgeNavigationStatus.NotAtStation; }
        public void ClearUi() => Value = null;
    }
}

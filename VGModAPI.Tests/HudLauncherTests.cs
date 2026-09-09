using System;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class HudLauncherTests
{
    [Fact]
    public void ExistingConstructorRemainsTextOnlyInBottomRight()
    {
        var button = new HudButton("Existing", "tip", false);
        Assert.Equal(HudCorner.BottomRight, button.Corner); Assert.Null(button.Icon); Assert.False(button.Enabled);
        Assert.NotNull(typeof(HudButton).GetConstructor(new[] { typeof(string), typeof(string), typeof(bool) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudButton("Invalid", (HudCorner)123));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudButton("Invalid", HudCorner.TopRight, (HudIcon)123));
    }

    [Fact]
    public void InvalidCanvasGeometryNamesTheInvalidDimension()
    {
        Assert.Equal("canvasWidth", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HudLauncherLayout(HudCorner.TopRight, new[] { 40f }, -1, 1080)).ParamName);
        Assert.Equal("canvasHeight", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HudLauncherLayout(HudCorner.TopRight, new[] { 40f }, 1920, float.NaN)).ParamName);
    }

    [Fact]
    public void ProvidersShareOneCornerLayoutAndOneProviderCanUseDifferentCorners()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = new HudService(hub, (_, _) => { });
        using var yBottom = service.Register("Y", "bottom", _ => { });
        yBottom.Update(new HudButton("Y bottom", HudCorner.BottomRight, HudIcon.Refinery), null);
        using var yTop = service.Register("Y", "top", _ => { });
        yTop.Update(new HudButton("Y top", HudCorner.TopRight, HudIcon.Storage), null);
        using var xTop = service.Register("X", "top", _ => { });
        xTop.Update(new HudButton("X", HudCorner.TopRight, HudIcon.Storage), null);
        var top = service.Launchers(HudCorner.TopRight);
        Assert.Equal(new[] { "X", "Y" }, top.Select(entry => entry.Plugin));
        Assert.Equal("bottom", Assert.Single(service.Launchers(HudCorner.BottomRight)).Local);
        var layout = Layout(HudCorner.TopRight, top.Select(entry => Width(entry.Button!)).ToArray());
        Assert.Equal(2, layout.Slots.Count);
        Assert.True(layout.Slots[1].X + layout.Slots[1].Width + HudLauncherLayout.Gap <= layout.Slots[0].X);
        var bottom = Layout(HudCorner.BottomRight, 40);
        Assert.True(bottom.Y + bottom.Height <= layout.Y);
        yTop.Update(new HudButton("Moved", HudCorner.BottomLeft), null);
        Assert.Single(service.Launchers(HudCorner.TopRight)); Assert.Single(service.Launchers(HudCorner.BottomLeft));
        Assert.Single(service.Launchers(HudCorner.BottomRight));
    }

    [Fact]
    public void OrderingIsStableAcrossRegistrationOrderUpdatesAndReRegistration()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = new HudService(hub, (_, _) => { });
        var z = service.Register("z", "one", _ => { }, -1); z.Update(new("Z", HudCorner.TopLeft), null);
        var b = service.Register("a", "two", _ => { }); b.Update(new("B", HudCorner.TopLeft, HudIcon.Storage), null);
        var a = service.Register("a", "one", _ => { }); a.Update(new("A", HudCorner.TopLeft), null);
        Assert.Equal(new[] { "Z", "A", "B" }, service.Launchers(HudCorner.TopLeft).Select(entry => entry.Button!.Label));
        a.Dispose(); a = service.Register("a", "one", _ => { }); a.Update(new("A", HudCorner.TopLeft), null);
        Assert.Equal(new[] { "Z", "A", "B" }, service.Launchers(HudCorner.TopLeft).Select(entry => entry.Button!.Label));
        b.Update(new("B", HudCorner.TopLeft, HudIcon.Storage), new HudPanel("Panel", Array.Empty<HudRow>()));
        Assert.Equal(new[] { "Z", "A" }, service.Launchers(HudCorner.TopLeft).Select(entry => entry.Button!.Label));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(640, 480)]
    [InlineData(320, 240)]
    [InlineData(160, 132)]
    public void AllCornerViewportsStayDisjointAndInsideCanvas(float width, float height)
    {
        var lanes = Enum.GetValues<HudCorner>().Select(corner => new HudLauncherLayout(corner,
            Enumerable.Repeat(40f, 16), width, height)).Where(lane => lane.Visible).ToArray();
        Assert.Equal(4, lanes.Length);
        foreach (var lane in lanes)
        {
            Assert.InRange(lane.X, 0, width - lane.Width); Assert.InRange(lane.Y, 0, height - lane.Height);
            foreach (var other in lanes.Where(other => !ReferenceEquals(other, lane)))
                Assert.True(lane.X + lane.Width <= other.X || other.X + other.Width <= lane.X ||
                    lane.Y + lane.Height <= other.Y || other.Y + other.Height <= lane.Y);
        }
    }

    [Theory]
    [InlineData(HudCorner.TopLeft)]
    [InlineData(HudCorner.TopRight)]
    [InlineData(HudCorner.BottomLeft)]
    [InlineData(HudCorner.BottomRight)]
    public void MixedIconsAndTextGetDisjointSlotsAndOverflowIsBounded(HudCorner corner)
    {
        var lane = Layout(corner, 40, 120, 40, 120, 40, 120, 40, 120);
        Assert.True(lane.Overflows); Assert.InRange(lane.Width, 40, 400);
        var ordered = lane.Slots.OrderBy(slot => slot.X).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            Assert.Equal(ordered[i - 1].X + ordered[i - 1].Width + HudLauncherLayout.Gap, ordered[i].X);
        Assert.Equal(0, ordered[0].X);
        Assert.Equal(lane.ContentWidth, ordered[^1].X + ordered[^1].Width);
        if (lane.Right) Assert.Equal(lane.ContentWidth - lane.Slots[0].Width, lane.Slots[0].X);
        else Assert.Equal(0, lane.Slots[0].X);
        Assert.False(new HudLauncherLayout(corner, new[] { 40f }, 20, 20).Visible);
    }

    [Fact]
    public void CrowdedInstallKeepsEveryProviderInBoundedScrollableLanes()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = new HudService(hub, (_, _) => { });
        for (var i = 0; i < 80; i++)
            service.Register("mod" + i.ToString("D2"), "launcher", _ => { })
                .Update(new HudButton("Window", (HudCorner)(i % 4), HudIcon.Storage), null);
        Assert.Equal(80, service.Entries.Count);
        foreach (var corner in Enum.GetValues<HudCorner>())
        {
            var entries = service.Launchers(corner); Assert.Equal(20, entries.Count);
            var layout = Layout(corner, entries.Select(entry => Width(entry.Button!)).ToArray());
            Assert.True(layout.Overflows); Assert.InRange(layout.Width, 40, 400);
            Assert.Equal(entries.Count, layout.Slots.Count);
        }
    }

    [Fact]
    public void CornerAndVisualChangesInvalidateOldInputRevision()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session); hub.GameplayInitialized(session);
        using var service = new HudService(hub, (_, _) => { }); service.SetAvailable(true);
        var surface = Guid.NewGuid(); service.SetSurface(surface, session);
        var calls = 0;
        using var registration = service.Register("owner", "one", _ => calls++);
        registration.Update(new("Window", HudCorner.TopRight, HudIcon.Storage), null);
        var entry = Assert.Single(service.Entries); var old = entry.Revision;
        registration.Update(new("Window", HudCorner.BottomRight, HudIcon.Refinery), null);
        Assert.False(service.Invoke(entry.Token, surface, old, HudInteractionKind.Button));
        Assert.True(service.Invoke(entry.Token, surface, entry.Revision, HudInteractionKind.Button));
        Assert.Equal(1, calls);
    }

    private static float Width(HudButton button) => button.Icon.HasValue ? HudLauncherLayout.IconWidth : HudLauncherLayout.TextWidth;
    private static HudLauncherLayout Layout(HudCorner corner, params float[] widths) => new(corner, widths, 1920, 1080);
}

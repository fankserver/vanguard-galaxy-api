using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModSettingsPresenterTests
{
    [Fact]
    public void PresentsAndChangesTheActualPublishedValues()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = new ModSettingsService(hub, (instance, caller) => new StoryHostPlugin((string)instance, caller));
        using var provider = service.Acquire("echo", Assembly.GetExecutingAssembly())!;
        var enabled = false;
        var range = 20;
        provider.Register(new BoolModSetting("enabled", "Autopilot", "Enabled", "Master switch.", false, () => enabled, value => enabled = value, order: 1));
        provider.Register(new IntModSetting("range", "Autopilot", "Range", "Scan range.", 20, 20, 30, 5, () => range, value => range = value, order: 2));
        var presenter = new ModSettingsPresenter(service);
        Assert.True(presenter.HasSettings("echo"));
        Assert.True(presenter.Open("echo"));
        Assert.Contains("current: Off", presenter.Details());
        Assert.Equal("Enabled", presenter.RowName(0));
        Assert.Equal("Off", presenter.RowValue(0));
        Assert.True(presenter.TryBool(0, out var initial));
        Assert.False(initial);
        Assert.True(presenter.SetBool(0, true));
        Assert.True(enabled);
        Assert.Equal("On", presenter.RowValue(0));
        Assert.True(presenter.TryNumber(1, out var current, out var minimum, out var maximum, out var step, out var wholeNumbers));
        Assert.Equal(20, current);
        Assert.Equal(20, minimum);
        Assert.Equal(30, maximum);
        Assert.Equal(5, step);
        Assert.True(wholeNumbers);
        Assert.False(presenter.TryNumber(0, out _, out _, out _, out _, out _));
        Assert.True(presenter.SetNumber(1, 24.9f));
        Assert.Equal(25, range);
        Assert.True(presenter.SetNumber(1, 99));
        Assert.Equal(30, range);
        Assert.True(presenter.ResetAll());
        Assert.False(enabled);
        Assert.Equal(20, range);
        Assert.True(presenter.Select(1));
        Assert.False(presenter.Select(2));
    }

    [Fact]
    public void ChoiceCyclingUsesLabelsAndRestartNotice()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = new ModSettingsService(hub, (instance, caller) => new StoryHostPlugin((string)instance, caller));
        using var provider = service.Acquire("echo", Assembly.GetExecutingAssembly())!;
        var mode = "tiered";
        provider.Register(new ChoiceModSetting("mode", "Autopilot", "Mode", "Deposit mode.", "tiered",
            new[] { new ModSettingChoice("off", "Off"), new ModSettingChoice("tiered", "Tiered"), new ModSettingChoice("always", "Always") },
            () => mode, value => mode = value, ModSettingApplyMode.RestartRequired));
        var presenter = new ModSettingsPresenter(service);
        Assert.True(presenter.Open("echo"));
        Assert.Contains("Applies after restart.", presenter.Details());
        Assert.Equal("Mode *", presenter.RowName(0));
        Assert.Equal("Tiered", presenter.RowValue(0));
        Assert.True(presenter.CycleChoice(0, -1));
        Assert.Equal("off", mode);
        Assert.Equal("Off", presenter.RowValue(0));
        mode = "removed-value";
        Assert.True(presenter.CycleChoice(0, 1));
        Assert.Equal("tiered", mode);
        Assert.False(presenter.SetBool(0, true));
    }
}

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
        Assert.Contains("Current: Off", presenter.Details());
        Assert.Equal("Toggle", presenter.DecreaseLabel);
        Assert.False(presenter.ShowIncrease);
        Assert.True(presenter.Change(-1));
        Assert.True(enabled);
        Assert.Equal("Enabled", presenter.RowName(0));
        Assert.Equal("On", presenter.RowValue(0));
        Assert.True(presenter.Select(1));
        Assert.False(presenter.Select(2));
        Assert.Equal("20", presenter.ValueLabel());
        Assert.True(presenter.ShowIncrease);
        Assert.True(presenter.Change(1));
        Assert.Equal(25, range);
        Assert.True(presenter.Reset());
        Assert.Equal(20, range);
    }

    [Fact]
    public void ChoiceNavigationUsesLabelsAndRestartNotice()
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
        Assert.Equal("Tiered (restart)", presenter.RowValue(0));
        Assert.Equal("Previous value", presenter.DecreaseLabel);
        Assert.Equal("Next value", presenter.IncreaseLabel);
        Assert.True(presenter.Change(-1));
        Assert.Equal("off", mode);
        Assert.Equal("Off", presenter.ValueLabel());
        mode = "removed-value";
        Assert.True(presenter.Change(1));
        Assert.Equal("tiered", mode);
    }
}

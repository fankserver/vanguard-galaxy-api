using System;
using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModSettingsServiceTests
{
    private static readonly Assembly Assembly = typeof(ModSettingsServiceTests).Assembly;

    private static ModSettingsService Service(LifecycleHub hub, List<Exception>? failures = null) =>
        new(hub, (instance, caller) => new StoryHostPlugin((string)instance, caller));

    [Fact]
    public void ProviderPublishesOnlyExplicitTypedSettingsUnderAuthenticatedIdentity()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        using var provider = service.Acquire("echo", Assembly)!;
        var enabled = false;
        Assert.Equal("echo", provider.ProviderId);
        Assert.Equal(ModSettingRegistrationStatus.Registered, provider.Register(new BoolModSetting(
            "auto-refine", "Autopilot", "Auto-refine on dock", "Enable station auto-refining.", false,
            () => enabled, value => enabled = value)));
        var setting = Assert.Single(service.Snapshot("echo"));
        Assert.Equal(ModSettingKind.Bool, setting.Kind);
        Assert.True(service.TryRead(setting, out var initial));
        Assert.Equal(false, initial);
        Assert.True(service.TryWrite(setting, true));
        Assert.True(enabled);
        Assert.True(service.TryReset(setting));
        Assert.False(enabled);
    }

    [Fact]
    public void DuplicateInvalidAndForeignDefinitionsAreRefusedWithoutMutation()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        using var provider = service.Acquire("echo", Assembly)!;
        var value = 2;
        var valid = new IntModSetting("hops", "Autopilot", "Maximum hops", "Search radius.", 2, 1, 10, 1,
            () => value, next => value = next);
        Assert.Equal(ModSettingRegistrationStatus.Registered, provider.Register(valid));
        Assert.Equal(ModSettingRegistrationStatus.Duplicate, provider.Register(valid));
        Assert.Equal(ModSettingRegistrationStatus.InvalidDefinition, provider.Register(new IntModSetting(
            "bad", "Autopilot", "Bad", "Bad range.", 2, 10, 1, 0, () => value, next => value = next)));
        var setting = Assert.Single(service.Snapshot("echo"));
        Assert.False(service.TryWrite(setting, 11));
        Assert.False(service.TryWrite(setting, "2"));
        Assert.Equal(2, value);
    }

    [Fact]
    public void ChoiceValuesAreStableAndPlayerLabelsNeedNotBeIdentifiers()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        using var provider = service.Acquire("echo", Assembly)!;
        var value = "tiered";
        Assert.Equal(ModSettingRegistrationStatus.Registered, provider.Register(new ChoiceModSetting(
            "stack-mode", "Autopilot", "Stack deposit", "Deposit amount per cycle.", "tiered",
            new[] { new ModSettingChoice("off", "Off"), new ModSettingChoice("tiered", "Tiered (recommended)"), new ModSettingChoice("always", "Always") },
            () => value, next => value = next)));
        var setting = Assert.Single(service.Snapshot("echo"));
        Assert.True(service.TryWrite(setting, "always"));
        Assert.Equal("always", value);
        Assert.False(service.TryWrite(setting, "Always"));
        Assert.Equal("always", value);
    }

    [Fact]
    public void CallbackFailuresAndUnconfirmedWritesAreIsolatedAndReported()
    {
        var failures = new List<Exception>();
        using var hub = new LifecycleHub((_, error) => failures.Add(error));
        using var service = Service(hub, failures);
        using var provider = service.Acquire("echo", Assembly)!;
        Assert.Equal(ModSettingRegistrationStatus.Registered, provider.Register(new BoolModSetting(
            "broken-read", "General", "Broken read", "Fails.", false, () => throw new InvalidOperationException("read"), _ => { })));
        Assert.Equal(ModSettingRegistrationStatus.Registered, provider.Register(new BoolModSetting(
            "ignored-write", "General", "Ignored write", "Does not apply.", false, () => false, _ => { })));
        var settings = service.Snapshot("echo");
        Assert.False(service.TryRead(settings[0], out _));
        Assert.False(service.TryWrite(settings[1], true));
        Assert.Single(failures);
    }

    [Fact]
    public void TinyFloatStepsDoNotLetNoOpSettersConfirmChanges()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        using var provider = service.Acquire("echo", Assembly)!;
        var value = 0f;
        provider.Register(new FloatModSetting("fine", "General", "Fine", "Fine adjustment.", 0f,
            0f, 1f, 0.00001f, () => value, _ => { }));
        var setting = Assert.Single(service.Snapshot("echo"));
        Assert.False(service.TryWrite(setting, 0.00001f));
        Assert.Equal(0f, value);
    }

    [Fact]
    public void DisposalRemovesPublishedSettingsAndClosesProvider()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        var provider = service.Acquire("echo", Assembly)!;
        provider.Register(new FloatModSetting("range", "Autopilot", "Range", "Maximum range.", 80, 20, 500, 5,
            () => 80, _ => { }));
        var retained = Assert.Single(service.Snapshot("echo"));
        provider.Dispose();
        Assert.Empty(service.Snapshot("echo"));
        Assert.False(service.TryRead(retained, out _));
        Assert.Equal(ModSettingRegistrationStatus.Unavailable, provider.Register(new BoolModSetting(
            "later", "General", "Later", "Too late.", false, () => false, _ => { })));
    }

    [Fact]
    public void AccessIsMainThreadOnlyAndShutdownReportsStopped()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var service = Service(hub);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => service.Snapshot("echo")));
        hub.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
        Assert.Null(service.Acquire("echo", Assembly));
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

[CollectionDefinition("service-root", DisableParallelization = true)]
public sealed class ServiceRootCollection { }

[Collection("service-root")]
public sealed class ServiceRootTests
{
    private static void Publish(ModServices root) => typeof(ModApi).GetMethod("PublishServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { root });
    private static void Clear(ModServices root) => typeof(ModApi).GetMethod("ClearServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { root });
    private static ModServices Compose(LifecycleHub hub, ModInformationCatalog catalog)
    {
        var lifecycle = new LifecycleServiceView(hub);
        var mods = catalog;
        var missions = new MissionServiceView(hub, null);
        var travel = new TravelServiceView(hub, null);
        var station = new StationServiceView(hub, null);
        foreach (var disposable in new IDisposable[] { lifecycle, mods, missions, travel, station }) hub.Services.AfterStopped(disposable.Dispose);
        return (ModServices)typeof(ModServices).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0].Invoke(
            new object[] { lifecycle, mods, new SaveDataServiceView(hub, null), missions, travel, station });
    }

    [Fact]
    public void PublicationIsNonNullableThreadBoundAndExactInstanceClearingPreservesNewLifetime()
    {
        Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(hub, () => Array.Empty<LoadedPluginInformation>());
        var root = Compose(hub, catalog);
        using var otherHub = new LifecycleHub((_, _) => { });
        using var otherCatalog = new ModInformationCatalog(otherHub, () => Array.Empty<LoadedPluginInformation>());
        var other = Compose(otherHub, otherCatalog);
        try
        {
            Publish(root);
            Assert.Same(root, ModApi.Services);
            Assert.True(root.Mods.Availability.IsAvailable);
            Assert.False(root.SaveData.Availability.IsAvailable);
            Assert.False(root.Missions.Availability.IsAvailable);
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = ModApi.Services));
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = root.Mods));
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Publish(other)).InnerException);
            Clear(root); hub.Dispose();
            Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
            Assert.Equal(ServiceUnavailableReason.ApiStopped, root.Mods.Availability.Reason);
            Publish(other);
            Clear(root);
            Assert.Same(other, ModApi.Services);
        }
        finally { Clear(root); Clear(other); }
    }

    [Fact]
    public void MenuCleanupUsesRetainedInventoryAfterRootShutdown()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(hub, () => Array.Empty<LoadedPluginInformation>());
        var root = Compose(hub, catalog);
        try
        {
            Publish(root);
            var inventory = ModApi.Services.Mods;
            inventory.Refresh();
            Clear(root);
            hub.Dispose();
            Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
            var cleanup = new List<string>();
            ModInventoryStatus? refreshed = null;
            Qualification.ProbeCleanup.Run(
                () => cleanup.Add("metadata"),
                () => { refreshed = inventory.Refresh().Status; cleanup.Add("inventory"); },
                () => cleanup.Add("devices"),
                () => cleanup.Add("focus"));
            Assert.Equal(ModInventoryStatus.Stopped, refreshed);
            Assert.Equal(new[] { "metadata", "inventory", "devices", "focus" }, cleanup);
        }
        finally { Clear(root); }
    }

    [Fact]
    public void DeferredViewCleanupPreservesTerminalCallbacksAndIsolatesCleanupFailures()
    {
        var failures = new List<Exception>();
        using var hub = new LifecycleHub((_, error) => failures.Add(error));
        hub.SetCapability("session-lifecycle", true, "Bound.");
        using var view = new LifecycleServiceView(hub);
        var seen = new List<string>();
        view.Changed += fact => { if (fact.Kind == LifecycleEventKind.SessionStarting) hub.Dispose(); };
        view.Changed += fact => seen.Add(fact.Kind.ToString());
        hub.Services.AfterStopped(() => throw new Exception());
        hub.Services.AfterStopped(() => { seen.Add("cleanup"); view.Dispose(); });
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(new[] { "SessionInvalidated", "cleanup" }, seen);
        Assert.Single(failures);
        Assert.Throws<ObjectDisposedException>(() => view.Changed += _ => { });
    }
}

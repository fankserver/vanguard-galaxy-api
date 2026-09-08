using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModInformationServiceTests
{
    private static LoadedPluginInformation Plugin() => new(ModApi.PluginId, "API", new Version(1, 0),
        Path.Combine(Path.GetTempPath(), "api.dll"), Array.Empty<ModDependencyInformation>());

    [Fact]
    public void InventoryAndLegacyRowsShareOneSourceWithExplicitStartupFreshness()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(() => new[] { Plugin() }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        var changes = new List<ModInventorySnapshot>();
        service.InventoryChanged += changes.Add;
        Assert.True(service.Availability.IsAvailable);
        Assert.False(service.Menu.Availability.IsAvailable);
        Assert.Equal(ModInventoryStatus.NotCollected, service.Inventory.Status);
        Assert.Empty(changes);
        Assert.Equal(ModInventoryStatus.Partial, service.Refresh().Status);
        Assert.Same(service.Inventory.Entries, catalog.Snapshot);
        catalog.MarkLoaderComplete();
        Assert.Equal(ModInventoryStatus.Partial, service.Inventory.Status);
        catalog.Refresh(); // Legacy refresh also updates modern state and notifications.
        Assert.Equal(ModInventoryStatus.Current, service.Inventory.Status);
        Assert.Equal(2, changes.Count);
        Assert.Same(changes[1], service.Inventory);
    }

    [Fact]
    public void FailedRefreshRetainsRowsWithoutClaimingFreshnessAndLegacyStillThrows()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var fail = false;
        using var catalog = new ModInformationCatalog(() => fail ? throw new IOException("private path") : new[] { Plugin() }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        catalog.MarkLoaderComplete();
        var current = service.Refresh();
        fail = true;
        Assert.Throws<IOException>(catalog.Refresh);
        Assert.Equal(ModInventoryStatus.RefreshFailed, service.Inventory.Status);
        Assert.Single(service.Inventory.Entries);
        Assert.Same(current.Entries[0], service.Inventory.Entries[0]);
        Assert.Equal("IOException", service.Inventory.Detail);
        Assert.Equal(ModInventoryStatus.RefreshFailed, service.Refresh().Status);
        Assert.True(service.Availability.IsAvailable);
        fail = false;
        Assert.Equal(ModInventoryStatus.Current, service.Refresh().Status);
    }

    [Fact]
    public void RefreshFromNotificationDoesNotRecurseAndSubscriberFailuresAreIsolated()
    {
        var errors = new List<Exception>();
        using var hub = new LifecycleHub((_, error) => errors.Add(error));
        var collections = 0;
        using var catalog = new ModInformationCatalog(() => { collections++; return new[] { Plugin() }; }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        var scopes = new List<bool>();
        service.InventoryChanged += _ => throw new Exception();
        service.InventoryChanged += snapshot => scopes.Add(hub.IsDispatchingCallbacks && ReferenceEquals(snapshot, service.Refresh()));
        service.Refresh();
        Assert.Equal(1, collections);
        Assert.Equal(new[] { true }, scopes);
        Assert.Single(errors);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void ApiShutdownProducesStoppedInventoryAndClosesRegistration()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(() => new[] { Plugin() }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        service.Refresh();
        var stopped = new List<ModInventorySnapshot>();
        service.InventoryChanged += stopped.Add;
        hub.Dispose();
        Assert.Equal(ModInventoryStatus.Stopped, Assert.Single(stopped).Status);
        Assert.Equal(ModInventoryStatus.Stopped, service.Inventory.Status);
        Assert.Equal(ModInventoryStatus.Stopped, service.Refresh().Status);
        Assert.Empty(catalog.Snapshot);
        Assert.Equal(ServiceUnavailableReason.ApiStopped, service.Availability.Reason);
        Assert.Throws<ObjectDisposedException>(() => service.InventoryChanged += stopped.Add);
        service.InventoryChanged -= stopped.Add;
    }

    [Fact]
    public void DisposingCatalogDuringCollectionCannotRepublishHealthyInventory()
    {
        using var hub = new LifecycleHub((_, _) => { });
        ModInformationCatalog? source = null;
        using var catalog = source = new ModInformationCatalog(() => { source!.Dispose(); return new[] { Plugin() }; }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        Assert.Equal(ModInventoryStatus.Stopped, service.Refresh().Status);
        Assert.False(service.Availability.IsAvailable);
        Assert.Empty(catalog.Snapshot);
    }

    [Fact]
    public void InventoryAccessRefreshAndRegistrationRejectForeignThreads()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(() => new[] { Plugin() }, _ => null);
        using var service = new ModInformationServiceView(hub, catalog);
        foreach (Action action in new Action[] { () => _ = service.Inventory, () => service.Refresh(), () => _ = service.Menu,
            () => service.InventoryChanged += _ => { } })
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(action));
    }
}

using System;

namespace VGModAPI.Core;

internal sealed class ModInformationServiceView : ObservationServiceView, IModInformationService, IDisposable
{
    private static readonly ModInventorySnapshot Stopped = new(ModInventoryStatus.Stopped, Array.Empty<ModInformation>());
    private readonly ModInformationCatalog _catalog;
    private readonly IServiceStatus _menu;
    private readonly ServiceNotifications<ModInventorySnapshot> _events;
    private bool _disposed;

    internal ModInformationServiceView(LifecycleHub hub, ModInformationCatalog catalog) : base(hub, "mod-information")
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        catalog.CheckThread();
        _menu = hub.Services.Get("mod-information-menu");
        _events = new ServiceNotifications<ModInventorySnapshot>(hub.CheckThread, hub.ReportSubscriberFailure, hub.EnterServiceDispatch);
        catalog.InventoryChanged += OnInventoryChanged;
        AvailabilityChanged += OnAvailabilityChanged;
        hub.SetCapability("mod-information", catalog.Inventory.Status != ModInventoryStatus.Stopped,
            "Local loader inventory.", ServiceUnavailableReason.ObserverFault);
    }

    public IServiceStatus Menu { get { Hub.CheckThread(); return _menu; } }
    public ModInventorySnapshot Inventory
    { get { Hub.CheckThread(); return Hub.Services.IsStopping ? Stopped : _catalog.Inventory; } }
    public event Action<ModInventorySnapshot>? InventoryChanged
    {
        add
        {
            Hub.CheckThread();
            if (Inventory.Status == ModInventoryStatus.Stopped) throw new ObjectDisposedException(nameof(ModInformationServiceView));
            _events.Add(value);
        }
        remove => _events.Remove(value);
    }

    public ModInventorySnapshot Refresh()
    {
        Hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(ModInformationServiceView));
        if (Inventory.Status == ModInventoryStatus.Stopped) return Inventory;
        if (!Availability.IsAvailable)
            return new ModInventorySnapshot(ModInventoryStatus.RefreshFailed, Inventory.Entries, Availability.Detail);
        try { _catalog.Refresh(); }
        catch (Exception) { /* The catalog committed RefreshFailed; legacy callers still receive the exception. */ }
        return Inventory;
    }

    private void OnInventoryChanged(ModInventorySnapshot inventory)
    {
        if (inventory.Status == ModInventoryStatus.Stopped)
            Hub.SetCapability("mod-information", false, "Catalog stopped.", ServiceUnavailableReason.ObserverFault);
        _events.Publish(inventory);
    }

    private void OnAvailabilityChanged(ServiceAvailability state)
    {
        if (state.Reason == ServiceUnavailableReason.ApiStopped) _catalog.Dispose();
    }

    public void Dispose()
    {
        Hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        _catalog.InventoryChanged -= OnInventoryChanged;
        AvailabilityChanged -= OnAvailabilityChanged;
        _events.Dispose();
    }
}

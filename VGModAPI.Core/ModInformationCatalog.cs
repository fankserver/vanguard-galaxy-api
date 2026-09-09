using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal sealed class LoadedPluginInformation
{
    internal LoadedPluginInformation(string id, string name, Version version, string location,
        IEnumerable<ModDependencyInformation> dependencies)
    { Id = id; Name = name; Version = version; Location = location; Dependencies = dependencies.ToArray(); }
    internal string Id { get; }
    internal string Name { get; }
    internal Version Version { get; }
    internal string Location { get; }
    internal IReadOnlyList<ModDependencyInformation> Dependencies { get; }
}

internal sealed class ModInformationCatalog : IModInformationService, IDisposable
{
    private readonly Func<IEnumerable<LoadedPluginInformation>> _plugins;
    private readonly Func<string, byte[]?> _read;
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status, _menu;
    private readonly ServiceNotifications<ModInventorySnapshot> _events;
    private bool _disposed, _loaderComplete, _refreshing;
    private ModInventorySnapshot _inventory = new(ModInventoryStatus.NotCollected, Array.Empty<ModInformation>());
    private static readonly ModInventorySnapshot Stopped = new(ModInventoryStatus.Stopped, Array.Empty<ModInformation>());
    internal ModInformationCatalog(LifecycleHub hub, Func<IEnumerable<LoadedPluginInformation>> plugins, Func<string, byte[]?>? read = null)
    {
        _hub = hub; _plugins = plugins; _read = read ?? ReadFile;
        _status = hub.Services.Get("mod-information");
        _menu = hub.Services.Get("mod-information-menu");
        _events = new ServiceNotifications<ModInventorySnapshot>(hub.CheckThread, hub.ReportSubscriberFailure, hub.EnterServiceDispatch);
        _status.AvailabilityChanged += OnAvailabilityChanged;
        hub.SetCapability("mod-information", true, "Local loader inventory.");
    }
    public ServiceAvailability Availability => _status.Availability;
    public IServiceStatus Menu { get { CheckThread(); return _menu; } }
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public ModInventorySnapshot Inventory { get { CheckThread(); return _hub.Services.IsStopping ? Stopped : _inventory; } }
    public event Action<ModInventorySnapshot>? InventoryChanged
    {
        add
        {
            CheckThread();
            if (_disposed || _hub.Services.IsStopping) throw new ObjectDisposedException(nameof(ModInformationCatalog));
            _events.Add(value);
        }
        remove => _events.Remove(value);
    }
    private void OnAvailabilityChanged(ServiceAvailability state)
    { if (state.Reason == ServiceUnavailableReason.ApiStopped) Dispose(); }
    internal void CheckThread() => _hub.CheckThread();

    internal void MarkLoaderComplete()
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(ModInformationCatalog));
        _loaderComplete = true; // Existing partial rows need a new collection before claiming Current.
    }

    public ModInventorySnapshot Refresh()
    {
        CheckThread();
        if (_disposed || _hub.Services.IsStopping) return Inventory;
        // A refresh requested by its own notification observes the committed result.
        if (_refreshing) return Inventory;
        _refreshing = true;
        try
        {
            try
            {
                var collected = Collect(_loaderComplete);
                if (_disposed) return Inventory;
                _inventory = collected;
            }
            catch (Exception error)
            {
                if (!_disposed)
                {
                    _inventory = new ModInventorySnapshot(ModInventoryStatus.RefreshFailed, _inventory.Entries, error.GetType().Name);
                    _events.Publish(_inventory);
                }
                return Inventory;
            }
            _events.Publish(_inventory);
            return Inventory;
        }
        finally { _refreshing = false; }
    }

    private ModInventorySnapshot Collect(bool loaderComplete)
    {
        var rows = new List<ModInformation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var plugin in _plugins())
        {
            if (++count > 4096) throw new InvalidOperationException("Loader inventory exceeds the supported bound.");
            if (plugin.Id != ModApi.PluginId && !plugin.Dependencies.Any(d => d.PluginId == ModApi.PluginId)) continue;
            // Chainloader keys are unique. Refuse an inconsistent snapshot instead of picking an arbitrary DLL.
            if (!ids.Add(plugin.Id)) throw new InvalidOperationException("Duplicate loader identity.");
            ModAuthorMetadata? metadata = null;
            var status = ModMetadataStatus.Missing;
            try
            {
                var file = MetadataPath(plugin.Location, plugin.Id);
                var bytes = _read(file);
                if (bytes != null) { metadata = ModMetadataCodec.Parse(bytes, plugin.Id); status = ModMetadataStatus.Available; }
            }
            catch (Exception e) when (e is FormatException || e is DecoderFallbackException || e is EncoderFallbackException || e is ArgumentException || e is NotSupportedException)
            { status = ModMetadataStatus.Invalid; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
            { status = ModMetadataStatus.Unreadable; }
            rows.Add(new ModInformation(plugin.Id, plugin.Name, plugin.Version, plugin.Dependencies, metadata, status));
        }
        return new ModInventorySnapshot(loaderComplete ? ModInventoryStatus.Current : ModInventoryStatus.Partial, rows);
    }

    internal static string MetadataPath(string location, string id)
    {
        if (id.Length == 0 || id.Length > 128 || id.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c is not ('.' or '_' or '-')))
            throw new FormatException("Plugin identity is not metadata-filename safe.");
        if (!Path.IsPathRooted(location)) throw new FormatException("Loaded DLL location is unavailable.");
        var directory = Path.GetDirectoryName(location);
        if (string.IsNullOrEmpty(directory)) throw new FormatException("Loaded DLL location is unavailable.");
        return Path.Combine(directory, id + ".vgmod.json");
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var bytes = new MemoryStream();
            var buffer = new byte[1024];
            while (true)
            {
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length, ModMetadataCodec.MaxBytes + 1 - (int)bytes.Length));
                if (read == 0) return bytes.ToArray();
                bytes.Write(buffer, 0, read);
                if (bytes.Length > ModMetadataCodec.MaxBytes) throw new FormatException("Metadata is oversized.");
            }
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _inventory = Stopped;
        _hub.SetCapability("mod-information", false, "Catalog stopped.", ServiceUnavailableReason.ObserverFault);
        _status.AvailabilityChanged -= OnAvailabilityChanged;
        _events.Complete(_inventory);
    }
}

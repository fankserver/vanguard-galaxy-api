using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

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

internal sealed class ModInformationCatalog : IModInformationCatalog, IDisposable
{
    private readonly Func<IEnumerable<LoadedPluginInformation>> _plugins;
    private readonly Func<string, byte[]?> _read;
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private bool _disposed, _loaderComplete, _refreshing;
    internal ModInformationCatalog(Func<IEnumerable<LoadedPluginInformation>> plugins, Func<string, byte[]?>? read = null)
    { _plugins = plugins; _read = read ?? ReadFile; }
    internal ModInventorySnapshot Inventory { get; private set; } = new(ModInventoryStatus.NotCollected, Array.Empty<ModInformation>());
    internal event Action<ModInventorySnapshot>? InventoryChanged;
    public IReadOnlyList<ModInformation> Snapshot => Inventory.Entries;

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread) throw new InvalidOperationException("Catalog access requires its owning main thread.");
    }

    internal void MarkLoaderComplete()
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(ModInformationCatalog));
        _loaderComplete = true; // Existing partial rows need a new collection before claiming Current.
    }

    public void Refresh()
    {
        CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(ModInformationCatalog));
        // A refresh requested by its own notification observes the committed result.
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            try
            {
                var collected = Collect(_loaderComplete);
                if (_disposed) return;
                Inventory = collected;
            }
            catch (Exception error)
            {
                if (!_disposed)
                {
                    Inventory = new ModInventorySnapshot(ModInventoryStatus.RefreshFailed, Inventory.Entries, error.GetType().Name);
                    InventoryChanged?.Invoke(Inventory);
                }
                throw; // The legacy refresh contract still reports collection failure to its caller.
            }
            InventoryChanged?.Invoke(Inventory);
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
        Inventory = new ModInventorySnapshot(ModInventoryStatus.Stopped, Array.Empty<ModInformation>());
        InventoryChanged?.Invoke(Inventory);
    }
}

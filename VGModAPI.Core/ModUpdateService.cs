using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VGModAPI.Core;

internal enum ModUpdateState { NoSource, NotChecked, Checking, Current, Available, InstalledAhead, Invalid, Failed, RateLimited }

internal sealed class ModUpdateStatus
{
    internal ModUpdateState State { get; }
    internal ModUpdateFeed? LastSuccess { get; }
    internal DateTimeOffset? CheckedAt { get; }
    internal DateTimeOffset? RetryAt { get; }
    internal ModUpdateStatus(ModUpdateState state, ModUpdateFeed? feed = null, DateTimeOffset? checkedAt = null, DateTimeOffset? retryAt = null)
    { State = state; LastSuccess = feed; CheckedAt = checkedAt; RetryAt = retryAt; }
}

// All entry mutation and completion application happens in Pump/Sync/Request on the Unity thread.
// Workers perform bounded network/cache I/O only and publish immutable completion messages.
internal sealed class ModUpdateService : IDisposable
{
    private sealed class Entry
    {
        internal readonly ModInformation Mod;
        internal readonly string Key;
        internal bool Load = true, Wanted, Running;
        internal ModUpdateStatus Status = new ModUpdateStatus(ModUpdateState.NotChecked);
        internal Entry(ModInformation mod) { Mod = mod; Key = ModUpdateCache.Key(mod); }
    }
    private sealed class Completion
    {
        internal readonly Entry Entry;
        internal readonly bool Network;
        internal readonly ModUpdateStatus Status;
        internal readonly string? Origin;
        internal Completion(Entry entry, bool network, ModUpdateStatus status, string? origin) { Entry = entry; Network = network; Status = status; Origin = origin; }
    }
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _backoff = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Completion> _completed = new ConcurrentQueue<Completion>();
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly IModFeedTransport _transport;
    private readonly ModFeedClient _client;
    private readonly ModUpdateCache _cache;
    private readonly Func<DateTimeOffset> _clock;
    private int _running;
    private bool _disposed;
    internal bool Automatic { get; set; }
    internal bool Enabled { get; set; } = true;
    internal ModUpdateService(IModFeedTransport transport, ModUpdateCache cache, Func<DateTimeOffset>? clock = null)
    {
        _transport = transport; _cache = cache; _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _client = new ModFeedClient(transport, backoff: host => _backoff.TryGetValue(host, out var until) ? until - _clock() : (TimeSpan?)null);
    }

    internal void Sync(IReadOnlyList<ModInformation> mods)
    {
        if (_disposed) return;
        var ids = new HashSet<string>(mods.Take(4096).Select(m => m.PluginId), StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToArray()) _entries.Remove(id);
        foreach (var mod in mods.Take(4096))
        {
            if (mod.Metadata?.UpdateUrl == null) { _entries.Remove(mod.PluginId); continue; }
            if (_entries.TryGetValue(mod.PluginId, out var old) && old.Key == ModUpdateCache.Key(mod) && old.Mod.InstalledVersion == mod.InstalledVersion) continue;
            _entries[mod.PluginId] = new Entry(mod);
        }
    }
    internal ModUpdateStatus Status(ModInformation mod) => _entries.TryGetValue(mod.PluginId, out var entry)
        ? entry.Status : new ModUpdateStatus(mod.Metadata?.UpdateUrl == null ? ModUpdateState.NoSource : ModUpdateState.NotChecked);

    internal bool Request(string pluginId)
    {
        if (_disposed || !Enabled || !_entries.TryGetValue(pluginId, out var entry) || entry.Status.State == ModUpdateState.Checking || entry.Wanted ||
            entry.Status.RetryAt > _clock()) return false;
        entry.Wanted = true;
        return true;
    }

    internal void Pump()
    {
        if (_disposed) return;
        while (_completed.TryDequeue(out var result))
        {
            --_running;
            var entry = result.Entry;
            entry.Running = false;
            if (result.Network && result.Status.State == ModUpdateState.RateLimited)
            {
                _backoff[new Uri(entry.Mod.Metadata!.UpdateUrl!).Host] = result.Status.RetryAt!.Value;
                if (result.Origin != null) _backoff[result.Origin] = result.Status.RetryAt!.Value;
            }
            if (!_entries.TryGetValue(entry.Mod.PluginId, out var current) || !ReferenceEquals(entry, current)) continue;
            entry.Status = result.Status;
        }
        var now = _clock();
        foreach (var entry in _entries.Values)
        {
            if (_running >= 2) break;
            if (entry.Running) continue;
            var source = entry.Mod.Metadata!.UpdateUrl!;
            if (!ModUpdateHosts.Allowed(source)) { entry.Load = false; entry.Wanted = false; entry.Status = new ModUpdateStatus(ModUpdateState.Invalid); continue; }
            var network = !entry.Load;
            if (network)
            {
                if (!Enabled) { entry.Wanted = false; continue; }
                if (!entry.Wanted && !Automatic) continue;
                if (!entry.Wanted && entry.Status.CheckedAt?.AddHours(6) > now &&
                    entry.Status.State is ModUpdateState.Current or ModUpdateState.Available or ModUpdateState.InstalledAhead) continue;
                if (entry.Status.RetryAt > now) continue;
                if (_backoff.TryGetValue(new Uri(source).Host, out var until) && until > now)
                {
                    entry.Status = new ModUpdateStatus(ModUpdateState.RateLimited, entry.Status.LastSuccess, entry.Status.CheckedAt, until);
                    continue;
                }
                entry.Wanted = false;
            }
            entry.Load = false;
            entry.Running = true;
            ++_running;
            var previous = entry.Status;
            if (network) entry.Status = new ModUpdateStatus(ModUpdateState.Checking, previous.LastSuccess, previous.CheckedAt);
            _ = Work(entry, network, previous);
        }
    }

    private Task Work(Entry entry, bool network, ModUpdateStatus previous) => Task.Run(async () =>
    {
        ModUpdateStatus status;
        string? origin = null;
        try
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (!network)
            {
                var cached = _cache.Read(entry.Mod, _clock());
                status = cached == null ? previous : Success(entry.Mod, cached.Value.Feed, cached.Value.Time);
            }
            else
            {
                var feed = await _client.FetchAsync(entry.Mod.Metadata!.UpdateUrl!, entry.Mod.PluginId, entry.Mod.Metadata.Channel, _stop.Token).ConfigureAwait(false);
                var time = _clock();
                status = Success(entry.Mod, feed, time);
                _cache.Write(entry.Mod, feed, time);
            }
        }
        catch (FormatException) { status = new ModUpdateStatus(ModUpdateState.Invalid, previous.LastSuccess, previous.CheckedAt, _clock().AddMinutes(15)); }
        catch (ModFeedRequestException ex) { origin = ex.Origin; status = new ModUpdateStatus(ex.RateLimited ? ModUpdateState.RateLimited : ModUpdateState.Failed, previous.LastSuccess, previous.CheckedAt, _clock().Add(ex.RetryAfter)); }
        catch (Exception) { status = new ModUpdateStatus(ModUpdateState.Failed, previous.LastSuccess, previous.CheckedAt, _clock().AddMinutes(15)); }
        _completed.Enqueue(new Completion(entry, network, status, origin));
    });

    private static ModUpdateStatus Success(ModInformation mod, ModUpdateFeed feed, DateTimeOffset time)
    {
        var comparison = Normalize(mod.InstalledVersion).CompareTo(Normalize(feed.Version));
        return new ModUpdateStatus(comparison == 0 ? ModUpdateState.Current : comparison < 0 ? ModUpdateState.Available : ModUpdateState.InstalledAhead,
            feed, time, time.AddMinutes(1));
    }
    private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Enabled = false;
        Automatic = false;
        _stop.Cancel();
        _transport.Dispose();
        _entries.Clear();
        // Workers retain the cancellation source until their bounded I/O completes.
    }
}

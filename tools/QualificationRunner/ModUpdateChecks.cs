using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;

namespace VGModAPI.Qualification;

// Shared by host tests and the opt-in Unity driver. Controlled faults are not wire evidence.
internal static class ModUpdateChecks
{
    internal const string Id = "vgmodapi.qualification";
    internal const string Release = "https://github.com/fankserver/vanguard-galaxy-api/releases";
    private const string Source = "https://raw.githubusercontent.com/controlled/fixture/main/update.json";
    internal static byte[] Feed(string channel = "stable", string version = "0.1.0") => Encoding.UTF8.GetBytes(
        "{\"schemaVersion\":1,\"pluginId\":\"" + Id + "\",\"version\":\"" + version + "\",\"channel\":\"" + channel + "\",\"releaseUrl\":\"" + Release + "\"}");
    internal static ModInformation Mod(string source = Source, string channel = "stable", string version = "0.1.0") =>
        new ModInformation(Id, "Controlled qualification", Version.Parse(version), Array.Empty<ModDependencyInformation>(),
            new ModAuthorMetadata(null, null, null, source, channel), ModMetadataStatus.Available);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task Until(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Update qualification condition timed out.");
            await Task.Delay(10);
        }
    }
    private static Task State(ModUpdateService service, ModInformation mod, ModUpdateState state) =>
        Until(() => { service.Pump(); return service.Status(mod).State == state; });
    private static async Task Quiet(ModUpdateService service)
    { for (var i = 0; i < 20; i++) { service.Pump(); await Task.Delay(10); } }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class Controlled : IModFeedTransport
    {
        internal Func<Uri, CancellationToken, Task<ModFeedResponse>> Reply = (_, _) => Task.FromResult(Response(Feed()));
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal bool Disposed;
        public Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken cancellation)
        { Interlocked.Increment(ref _calls); return Reply(uri, cancellation); }
        public void Dispose() { Disposed = true; }
    }
    private static ModFeedResponse Response(byte[] bytes) => new ModFeedResponse(200, new MemoryStream(bytes));

    internal static async Task ControlledAsync(string root, Action<string> record)
    {
        var now = DateTimeOffset.UtcNow;
        var mod = Mod();
        var cache = new ModUpdateCache(Path.Combine(root, "controlled"));
        var transport = new Controlled();
        using (var service = new ModUpdateService(transport, cache, () => now))
        {
            Check(!service.Automatic && service.Enabled, "Default update settings changed.");
            service.Sync(new[] { mod }); await Quiet(service);
            Check(transport.Calls == 0, "Default settings performed a request.");
            Check(service.Request(Id) && !service.Request(Id), "Manual request did not coalesce.");
            await State(service, mod, ModUpdateState.Current);
            Check(transport.Calls == 1 && !service.Request(Id), "Manual cooldown failed.");
            record("controlled-default-manual-coalescing-cooldown");
            service.Automatic = true;
            now = service.Status(mod).CheckedAt!.Value.AddHours(6).AddSeconds(-1);
            await Quiet(service); Check(transport.Calls == 1, "Automatic interval ran early.");
            now = now.AddSeconds(1); await State(service, mod, ModUpdateState.Current);
            Check(transport.Calls == 2, "Automatic interval failed.");
            service.Enabled = false; Check(!service.Request(Id), "Disabled service accepted a request.");
            service.Enabled = true; service.Automatic = false;
            record("controlled-six-hour-automatic-disable");

            foreach (var fault in new Exception[] { new HttpRequestException("Controlled DNS failure"), new System.Security.Authentication.AuthenticationException("Controlled TLS failure"), new OperationCanceledException("Controlled timeout") })
            {
                now = now.AddHours(1);
                transport.Reply = (_, _) => Task.FromException<ModFeedResponse>(fault);
                Check(service.Request(Id), "Failure request was rejected.");
                await State(service, mod, ModUpdateState.Failed);
                Check(service.Status(mod).LastSuccess != null && service.Status(mod).CheckedAt < now, "Failure erased last-known success.");
            }
            record("controlled-dns-tls-timeout-retain-last-success");
            now = now.AddHours(1);
            transport.Reply = (_, _) => Task.FromResult(new ModFeedResponse(429, Stream.Null, retryAfter: TimeSpan.FromMinutes(7)));
            Check(service.Request(Id), "Rate-limit request rejected.");
            await State(service, mod, ModUpdateState.RateLimited);
            Check(service.Status(mod).RetryAt == now.AddMinutes(7) && !service.Request(Id), "Rate-limit deadline failed.");
            record("controlled-rate-limit");
        }
        Check(transport.Disposed, "Service did not dispose its transport.");
        foreach (var pair in new[] { ("0.1.0", ModUpdateState.Current), ("0.0.9", ModUpdateState.Available), ("0.2.0", ModUpdateState.InstalledAhead) })
        {
            var cached = Mod(version: pair.Item1);
            var noNetwork = new Controlled();
            using var reader = new ModUpdateService(noNetwork, cache, () => now);
            reader.Sync(new[] { cached }); await State(reader, cached, pair.Item2);
            Check(noNetwork.Calls == 0, "Cache read performed network I/O.");
        }
        Check(cache.Read(mod, now.AddDays(31)) == null && cache.Read(Mod(channel: "experimental"), now) == null, "Cache expiry/channel isolation failed.");
        record("controlled-disk-cache-expiry-channel-installed-version");

        using (var fake = new Controlled())
        {
            var client = new ModFeedClient(fake);
            foreach (var bytes in new[] { Encoding.UTF8.GetBytes("not json"), new byte[ModMetadataCodec.MaxBytes + 1], Feed("experimental") })
            {
                fake.Reply = (_, _) => Task.FromResult(Response(bytes));
                await Reject<FormatException>(() => client.FetchAsync(Source, Id, "stable", CancellationToken.None));
            }
            fake.Reply = (_, _) => Task.FromResult(new ModFeedResponse(302, Stream.Null, "https://unapproved.example/feed"));
            var before = fake.Calls;
            await Reject<FormatException>(() => client.FetchAsync(Source, Id, "stable", CancellationToken.None));
            Check(fake.Calls == before + 1, "Unapproved redirect was requested.");
            fake.Reply = (_, _) => Task.FromResult(new ModFeedResponse(302, Stream.Null, Source));
            before = fake.Calls;
            await Reject<FormatException>(() => client.FetchAsync(Source, Id, "stable", CancellationToken.None));
            Check(fake.Calls == before + 4, "Redirect hop bound changed.");
        }
        record("controlled-invalid-oversized-channel-redirect-policy");
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Controlled { Reply = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Unbounded request completed."); }
            catch (OperationCanceledException) { canceled.TrySetResult(true); throw; }
        } };
        var quitting = new ModUpdateService(pending, new ModUpdateCache(Path.Combine(root, "quit")));
        try
        {
            quitting.Sync(new[] { mod }); Check(quitting.Request(Id), "Quit request rejected.");
            await Until(() => { quitting.Pump(); return pending.Calls == 1; });
            quitting.Dispose();
            await Until(() => canceled.Task.IsCompleted);
            quitting.Pump();
            Check(pending.Disposed && quitting.Status(mod).State == ModUpdateState.NotChecked, "Quit applied a stale completion.");
        }
        finally { quitting.Dispose(); }
        record("controlled-quit-mid-check");
    }
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-updates-" + Guid.NewGuid());
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    private static ModInformation Mod(string version = "1.0", string source = "https://github.com/a/b/feed", string channel = "stable", string id = "a") =>
        new ModInformation(id, "A", Version.Parse(version), Array.Empty<ModDependencyInformation>(), new ModAuthorMetadata(null, null, null, source, channel), ModMetadataStatus.Available);
    private sealed class Transport : IModFeedTransport
    {
        internal int Calls;
        internal Func<Uri, CancellationToken, Task<ModFeedResponse>>? Handler;
        internal int Code = 200;
        internal TaskCompletionSource<bool>? Wait;
        public async Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            if (Handler != null) return await Handler(uri, cancellation);
            if (Wait != null) await Wait.Task.WaitAsync(cancellation);
            return new ModFeedResponse(Code, new MemoryStream(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"pluginId\":\"a\",\"channel\":\"stable\",\"version\":\"2.0\",\"releaseUrl\":\"https://github.com/a/b/releases\"}")), retryAfter: TimeSpan.FromMinutes(5));
        }
        public void Dispose() { }
    }
    private static async Task Until(ModUpdateService service, Func<bool> done)
    {
        for (var i = 0; i < 200; ++i) { service.Pump(); if (done()) return; await Task.Delay(5); }
        Assert.Fail("Scheduler did not reach expected state.");
    }
    [Theory]
    [InlineData("1.0", (int)ModUpdateState.Available)]
    [InlineData("2.0.0.0", (int)ModUpdateState.Current)]
    [InlineData("3.0", (int)ModUpdateState.InstalledAhead)]
    public async Task ManualResultsAreComparedWithCurrentInstalledVersion(string version, int expected)
    {
        var transport = new Transport();
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => Now);
        var mod = Mod(version); service.Sync(new[] { mod });
        Assert.True(service.Request("a")); Assert.False(service.Request("a"));
        await Until(service, () => (int)service.Status(mod).State == expected);
        Assert.Equal(1, transport.Calls);
        Assert.False(service.Request("a"));
    }
    [Fact]
    public async Task OfflineCacheDoesNotSendRequestsAndFailureKeepsLastSuccess()
    {
        var cache = new ModUpdateCache(_root); var mod = Mod();
        cache.Write(mod, new ModUpdateFeed(new Version(2, 0), "https://github.com/a/b/releases"), Now.AddDays(-1));
        var transport = new Transport { Code = 429 };
        using var service = new ModUpdateService(transport, cache, () => Now);
        service.Sync(new[] { mod });
        await Until(service, () => service.Status(mod).LastSuccess != null);
        Assert.Equal(0, transport.Calls);
        Assert.True(service.Request("a"));
        await Until(service, () => service.Status(mod).State == ModUpdateState.RateLimited);
        Assert.NotNull(service.Status(mod).LastSuccess);
        Assert.Equal(Now.AddMinutes(5), service.Status(mod).RetryAt);
    }
    [Fact]
    public async Task DisposalAndRemovedContextCannotPublishLateResults()
    {
        var transport = new Transport { Wait = new TaskCompletionSource<bool>() };
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => Now);
        var mod = Mod(); service.Sync(new[] { mod }); service.Request("a");
        await Until(service, () => transport.Calls == 1);
        service.Sync(Array.Empty<ModInformation>());
        service.Dispose(); transport.Wait.SetResult(true);
        await Task.Delay(20); service.Pump();
        Assert.Equal(ModUpdateState.NotChecked, service.Status(mod).State);
    }
    [Fact]
    public void CacheContextIncludesSourceChannelAndSchemaNotInstalledVersion()
    {
        var cache = new ModUpdateCache(_root); var mod = Mod();
        cache.Write(mod, new ModUpdateFeed(new Version(2, 0), "https://github.com/a/b/releases"), Now);
        Assert.NotNull(cache.Read(Mod("3.0"), Now));
        Assert.Null(cache.Read(Mod(source: "https://github.com/a/c/feed"), Now));
        Assert.Null(cache.Read(Mod(channel: "experimental"), Now));
        Assert.Null(cache.Read(mod, Now.AddDays(31)));
        Assert.Null(cache.Read(mod, Now.AddSeconds(-1)));
    }
    [Fact]
    public void CacheFailureAndForeignTemporaryFileAreHarmless()
    {
        Directory.CreateDirectory(_root);
        var cache = new ModUpdateCache(_root); var mod = Mod();
        var temporary = Path.Combine(_root, ModUpdateCache.Key(mod) + ".cache.tmp");
        File.WriteAllText(temporary, "not ours");
        cache.Write(mod, new ModUpdateFeed(new Version(2, 0), "https://github.com/a/b/releases"), Now);
        Assert.Equal("not ours", File.ReadAllText(temporary));
        Assert.Null(cache.Read(mod, Now));
    }
    [Fact]
    public async Task ConcurrencyIsTwoAndGlobalDisableDropsPendingChecks()
    {
        var transport = new Transport { Wait = new TaskCompletionSource<bool>() };
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => Now);
        var mods = new[] { Mod(), Mod(id: "b"), Mod(id: "c") };
        service.Sync(mods);
        foreach (var mod in mods) Assert.True(service.Request(mod.PluginId));
        await Until(service, () => transport.Calls == 2);
        for (var i = 0; i < 5; ++i) { service.Pump(); await Task.Delay(5); }
        Assert.Equal(2, transport.Calls);
        service.Enabled = false;
        service.Pump(); // Both workers still occupied: pending cancellation cannot depend on capacity.
        service.Enabled = true;
        transport.Wait.SetResult(true);
        for (var i = 0; i < 20; ++i) { service.Pump(); await Task.Delay(5); }
        Assert.Equal(2, transport.Calls);
    }
    [Fact]
    public void CacheIsBoundedAndOversizedEntriesAreIgnored()
    {
        var cache = new ModUpdateCache(_root);
        for (var i = 0; i < 130; ++i)
            cache.Write(Mod(id: "id" + i), new ModUpdateFeed(new Version(2, 0), "https://github.com/a/b/releases"), Now);
        Assert.Equal(128, Directory.GetFiles(_root, "*.cache").Length);
        File.WriteAllBytes(Path.Combine(_root, ModUpdateCache.Key(Mod(id: "id129")) + ".cache"), new byte[17000]);
        Assert.Null(cache.Read(Mod(id: "id129"), Now));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRateLimitsKeepLongestSourceAndRedirectDeadline(bool redirect)
    {
        var now = Now;
        var first = new TaskCompletionSource<ModFeedResponse>();
        var second = new TaskCompletionSource<ModFeedResponse>();
        var transport = new Transport { Handler = (uri, _) => uri.AbsolutePath.EndsWith("first") ? first.Task : second.Task };
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => now);
        var a = Mod(source: "https://github.com/first");
        var b = Mod(source: "https://github.com/second", id: "b");
        service.Sync(new[] { a, b }); service.Request("a"); service.Request("b");
        await Until(service, () => transport.Calls == 2);
        if (redirect)
        {
            first.SetException(new ModFeedRequestException(true, TimeSpan.FromDays(1), "release-assets.githubusercontent.com"));
            await Until(service, () => service.Status(a).State == ModUpdateState.RateLimited);
            second.SetException(new ModFeedRequestException(true, TimeSpan.FromMinutes(1), "release-assets.githubusercontent.com"));
        }
        else
        {
            first.SetResult(new ModFeedResponse(429, Stream.Null, retryAfter: TimeSpan.FromDays(1)));
            await Until(service, () => service.Status(a).State == ModUpdateState.RateLimited);
            second.SetResult(new ModFeedResponse(429, Stream.Null, retryAfter: TimeSpan.FromMinutes(1)));
        }
        await Until(service, () => service.Status(b).State == ModUpdateState.RateLimited);
        now = now.AddMinutes(2);
        var c = Mod(source: redirect ? "https://release-assets.githubusercontent.com/third" : "https://github.com/third", id: "c");
        service.Sync(new[] { a, b, c }); service.Request("c");
        await Until(service, () => service.Status(c).State == ModUpdateState.RateLimited);
        Assert.Equal(Now.AddDays(1), service.Status(c).RetryAt);
        Assert.Equal(2, transport.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedUnicodeIsInvalidFeed(bool escapedSurrogate)
    {
        var bytes = escapedSurrogate ? Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"pluginId\":\"\\uD800\",\"channel\":\"stable\",\"version\":\"2.0\",\"releaseUrl\":\"https://github.com/a\"}") : new byte[] { 0xff };
        var transport = new Transport { Handler = (_, _) => Task.FromResult(new ModFeedResponse(200, new MemoryStream(bytes))) };
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => Now);
        var mod = Mod(); service.Sync(new[] { mod }); service.Request("a");
        await Until(service, () => service.Status(mod).State == ModUpdateState.Invalid);
    }
    [Fact]
    public async Task AutomaticSuccessWaitsSixHoursAndUnavailableHostIsFailure()
    {
        var now = Now; var transport = new Transport(); var mod = Mod();
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => now) { Automatic = true };
        service.Sync(new[] { mod });
        await Until(service, () => service.Status(mod).State == ModUpdateState.Available);
        now = now.AddHours(5); service.Pump(); Assert.Equal(1, transport.Calls);
        transport.Handler = (_, _) => throw new System.Net.Http.HttpRequestException("unavailable");
        now = now.AddHours(1);
        await Until(service, () => service.Status(mod).State == ModUpdateState.Failed);
        Assert.Equal(2, transport.Calls); Assert.NotNull(service.Status(mod).LastSuccess);
    }
    [Fact]
    public async Task ReplacedContextRejectsCompletionWithoutDisposingService()
    {
        var transport = new Transport { Wait = new TaskCompletionSource<bool>() };
        using var service = new ModUpdateService(transport, new ModUpdateCache(_root), () => Now);
        var mod = Mod(); service.Sync(new[] { mod }); service.Request("a");
        await Until(service, () => transport.Calls == 1);
        var changed = Mod(source: "https://github.com/a/new/feed"); service.Sync(new[] { changed });
        transport.Wait.SetResult(true);
        for (var i = 0; i < 30; ++i) { service.Pump(); await Task.Delay(5); }
        Assert.Equal(ModUpdateState.NotChecked, service.Status(changed).State);
        Assert.Null(service.Status(changed).LastSuccess);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdatePresenterTests
{
    private sealed class Offline : IModFeedTransport
    {
        public Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken cancellation) => throw new InvalidOperationException("No request expected.");
        public void Dispose() { }
    }
    private static ModInformation Mod(string source = "https://github.com/a/b/feed") => new ModInformation("a", "A", new Version(1, 0),
        Array.Empty<ModDependencyInformation>(), new ModAuthorMetadata(null, null, null, source, "stable"), ModMetadataStatus.Available);
    private static ModUpdateService Service() => new ModUpdateService(new Offline(), new ModUpdateCache(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
    [Fact]
    public void ManualCheckRequestsImmediatelyAndRepeatedClicksCoalesce()
    {
        using var service = Service(); var mod = Mod(); service.Sync(new[] { mod });
        var presenter = new ModUpdatePresenter(service);
        Assert.True(presenter.Check(mod));
        Assert.False(presenter.Check(mod));
        var text = presenter.Text(mod, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("confirm", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IP address", text);
        Assert.DoesNotContain("Automatic", text);
    }
    [Fact]
    public async Task CachedUpdateOffersItsReleaseWithoutNetworkAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-update-presenter-" + Guid.NewGuid().ToString("N"));
        const string release = "https://github.com/a/b/releases";
        var mod = Mod();
        try
        {
            var feed = ModUpdateFeed.Parse(System.Text.Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"pluginId\":\"a\",\"version\":\"2.0.0\",\"channel\":\"stable\",\"releaseUrl\":\"" + release + "\"}"), "a", "stable");
            var cache = new ModUpdateCache(root);
            cache.Write(mod, feed, DateTimeOffset.UtcNow);
            using var service = new ModUpdateService(new Offline(), cache);
            service.Sync(new[] { mod });
            for (var attempt = 0; attempt < 500 && service.Status(mod).State != ModUpdateState.Available; attempt++)
            { service.Pump(); await Task.Delay(10); }
            Assert.Equal(ModUpdateState.Available, service.Status(mod).State);
            var presenter = new ModUpdatePresenter(service);
            string? opened = null;
            Assert.True(presenter.OpenRelease(mod, destination => opened = destination));
            Assert.Equal(release, opened);
            Assert.Contains("Update available", presenter.Text(mod, DateTimeOffset.UtcNow));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void DisabledAndUnsupportedFeedsCannotCheckOrOpenRelease()
    {
        using var service = Service(); var presenter = new ModUpdatePresenter(service);
        Assert.False(presenter.Check(Mod("https://evil.example/feed")));
        service.Enabled = false;
        Assert.False(presenter.Check(Mod()));
        Assert.Contains("unavailable", presenter.Text(Mod(), DateTimeOffset.UtcNow));
        var opened = false; Assert.False(presenter.OpenRelease(Mod(), _ => opened = true)); Assert.False(opened);
    }
}

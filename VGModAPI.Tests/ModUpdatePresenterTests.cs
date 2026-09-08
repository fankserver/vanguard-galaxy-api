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

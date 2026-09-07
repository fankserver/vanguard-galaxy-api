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
    public void ManualConfirmationIsBoundToCurrentFeedAndCanBeCancelled()
    {
        using var service = Service(); var mod = Mod(); service.Sync(new[] { mod });
        var presenter = new ModUpdatePresenter(service, _ => { });
        Assert.False(presenter.Check(mod));
        var disclosure = presenter.Text(mod, DateTimeOffset.UtcNow);
        Assert.Contains("IP address", disclosure); Assert.Contains("No saves", disclosure);
        Assert.DoesNotContain("https://github.com/a/b/feed", disclosure);
        var changed = Mod("https://raw.githubusercontent.com/a/b/feed");
        Assert.False(presenter.Check(changed));
        presenter.Cancel(); Assert.False(presenter.Confirming);
        Assert.False(presenter.Check(mod)); Assert.True(presenter.Check(mod));
        Assert.False(presenter.Confirming);
    }
    [Fact]
    public void AutomaticOptInRequiresSeparateExplicitConfirmationAndPersists()
    {
        using var service = Service(); var persisted = false;
        var presenter = new ModUpdatePresenter(service, value => persisted = value);
        presenter.ToggleAutomatic(); Assert.False(persisted); Assert.False(service.Automatic);
        Assert.True(presenter.ConfirmingAutomatic);
        Assert.Contains("all API consumers", presenter.Text(Mod(), DateTimeOffset.UtcNow));
        presenter.ToggleAutomatic(); Assert.True(persisted); Assert.True(service.Automatic);
        presenter.ToggleAutomatic(); Assert.False(persisted); Assert.False(service.Automatic);
    }
    [Fact]
    public void DisabledAndUnsupportedFeedsCannotBeConfirmedAndNoReleaseCanOpen()
    {
        using var service = Service(); var presenter = new ModUpdatePresenter(service, _ => { });
        Assert.False(presenter.Check(Mod("https://evil.example/feed"))); Assert.False(presenter.Confirming);
        service.Enabled = false;
        Assert.False(presenter.Check(Mod())); Assert.False(presenter.Confirming);
        Assert.Contains("Disabled globally", presenter.Text(Mod(), DateTimeOffset.UtcNow));
        var opened = false; Assert.False(presenter.OpenRelease(Mod(), _ => opened = true)); Assert.False(opened);
    }
}

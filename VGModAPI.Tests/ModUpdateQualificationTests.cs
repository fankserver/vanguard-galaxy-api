using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using VGModAPI.Core;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateQualificationTests
{
    [Fact]
    public void WireHeartbeatCannotBorrowEarlierFramesOrAcceptAStall()
    {
        Assert.Throws<InvalidOperationException>(() => new ProbeHeartbeat(1000, 10).Complete(1001, 10.1));
        Assert.Throws<InvalidOperationException>(() => new ProbeHeartbeat(1000, 10).Complete(1100, 13));
        var valid = new ProbeHeartbeat(1000, 10);
        valid.Tick(10.5); valid.Tick(11); valid.Complete(1003, 11.5);
    }
    private sealed class BlockedWire : IModFeedTransport
    {
        internal int Calls;
        internal bool Disposed;
        internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken token)
        {
            ++Calls; Started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }
        public void Dispose() { Disposed = true; }
    }
    [Fact]
    public async Task TeardownDuringWireRequestCannotStartTheNextFixtureOrRecord()
    {
        using var stop = new CancellationTokenSource();
        var transport = new BlockedWire();
        var facts = new List<string>();
        var pending = ModUpdateWireChecks.RunAsync(new string('a', 40), facts.Add, stop.Token, transport);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, transport.Calls); Assert.Empty(facts); Assert.True(transport.Disposed);
    }
    private sealed class WrongSizeFixture : IModFeedTransport
    {
        public Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken token)
        {
            if (uri.Host == "github.com") return Task.FromResult(new ModFeedResponse(302, Stream.Null,
                "https://raw.githubusercontent.com/fixture/stable.json"));
            var malformed = uri.AbsolutePath.EndsWith("invalid.json") || uri.AbsolutePath.EndsWith("oversized.json");
            var bytes = malformed ? System.Text.Encoding.UTF8.GetBytes("{}") : ModUpdateChecks.Feed(uri.AbsolutePath.EndsWith("experimental.json") ? "experimental" : "stable");
            return Task.FromResult(new ModFeedResponse(200, new MemoryStream(bytes)));
        }
        public void Dispose() { }
    }
    [Fact]
    public async Task SchemaFailureCannotStandInForWireSizeEnforcement()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ModUpdateWireChecks.RunAsync(
            new string('a', 40), _ => { }, CancellationToken.None, new WrongSizeFixture()));
        Assert.Contains("oversized.json", error.Message);
    }
    [Fact]
    public async Task ControlledDriverRunsTheProductionCoreAndReportsEveryPhase()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-updates-" + Guid.NewGuid().ToString("N"));
        var steps = new List<string>();
        try
        {
            await ModUpdateChecks.ControlledAsync(root, steps.Add);
            Assert.Equal(7, steps.Count);
            Assert.Contains("controlled-quit-mid-check", steps);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

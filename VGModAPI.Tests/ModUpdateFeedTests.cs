using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateFeedTests
{
    private const string Url = "https://github.com/author/mod/releases/latest/download/update.json";
    private const string Json = "{\"schemaVersion\":1,\"pluginId\":\"author.mod\",\"version\":\"1.2\",\"releaseUrl\":\"https://github.com/author/mod/releases/tag/v1.2\",\"channel\":\"stable\"}";
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static ModFeedResponse Reply(int status = 200, string? location = null, byte[]? body = null) =>
        new(status, new MemoryStream(body ?? Bytes(Json)), location);
    private sealed class Transport : IModFeedTransport
    {
        internal readonly List<Uri> Requests = new();
        internal Func<Uri, CancellationToken, Task<ModFeedResponse>> Handler = (_, _) => Task.FromResult(Reply());
        public Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken token) { Requests.Add(uri); return Handler(uri, token); }
        public void Dispose() { }
    }

    [Fact]
    public void ParsesMatchingNumericVersionAndNormalizesMissingSegments()
    {
        var feed = ModUpdateFeed.Parse(Bytes(Json), "author.mod", "stable");
        Assert.Equal(new Version(1, 2, 0, 0), feed.Version);
        Assert.Equal("https://github.com/author/mod/releases/tag/v1.2", feed.ReleaseUrl);
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":\"1\"")]
    [InlineData("author.mod", "wrong.mod")]
    [InlineData("stable", "experimental")]
    [InlineData("\"1.2\"", "\"1.2-beta\"")]
    [InlineData("https://github.com/author/mod/releases/tag/v1.2", "http://github.com/mod")]
    [InlineData("https://github.com/author/mod/releases/tag/v1.2", "https://127.0.0.1/mod")]
    public void RejectsInvalidFeedFields(string before, string after) =>
        Assert.Throws<FormatException>(() => ModUpdateFeed.Parse(Bytes(Json.Replace(before, after)), "author.mod", "stable"));

    [Theory]
    [InlineData(",\"extra\":\"value\"}")]
    [InlineData(",\"version\":\"2.0\"}")]
    [InlineData(",\"extra\":{}}")]
    public void RejectsUnknownDuplicateOrNestedFields(string suffix) =>
        Assert.Throws<FormatException>(() => ModUpdateFeed.Parse(Bytes(Json.Substring(0, Json.Length - 1) + suffix), "author.mod", "stable"));

    [Theory]
    [InlineData("http://github.com/mod")]
    [InlineData("https://github.com.evil.example/mod")]
    [InlineData("https://github.com@evil.example/mod")]
    [InlineData("https://github.com:444/mod")]
    [InlineData("https://127.0.0.1/mod")]
    [InlineData("https://example.org/mod")]
    [InlineData("file:///tmp/feed")]
    public async Task RejectsUnsupportedSourceBeforeTransport(string source)
    {
        using var transport = new Transport();
        await Assert.ThrowsAsync<FormatException>(() => new ModFeedClient(transport).FetchAsync(source, "author.mod", "stable", default));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task SupportsBoundedRelativeAndSignedAssetRedirects()
    {
        using var transport = new Transport();
        transport.Handler = (_, _) => Task.FromResult(transport.Requests.Count switch
        {
            1 => Reply(302, "/author/mod/releases/download/v1.2/update.json"),
            2 => Reply(302, "https://release-assets.githubusercontent.com/asset?signature=fixture"),
            _ => Reply()
        });
        var result = await new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", default);
        Assert.Equal(new Version(1, 2, 0, 0), result.Version);
        Assert.Equal(3, transport.Requests.Count);
    }

    [Theory]
    [InlineData("http://github.com/mod")]
    [InlineData("https://localhost/mod")]
    [InlineData("https://user:password@github.com/mod")]
    [InlineData("https://other.example/mod")]
    public async Task ValidatesRedirectBeforeFollowing(string target)
    {
        using var transport = new Transport { Handler = (_, _) => Task.FromResult(Reply(302, target)) };
        await Assert.ThrowsAsync<FormatException>(() => new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", default));
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task RedirectLoopIsFinite()
    {
        using var transport = new Transport { Handler = (_, _) => Task.FromResult(Reply(302, Url)) };
        await Assert.ThrowsAsync<FormatException>(() => new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", default));
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task BoundsChunkedResponseWithoutContentLength()
    {
        using var transport = new Transport { Handler = (_, _) => Task.FromResult(Reply(body: new byte[16385])) };
        await Assert.ThrowsAsync<FormatException>(() => new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", default));
    }

    [Fact]
    public async Task DeadlineCancelsResponseHeaders()
    {
        using var transport = new Transport { Handler = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModFeedClient(transport, TimeSpan.FromMilliseconds(20)).FetchAsync(Url, "author.mod", "stable", default));
    }

    [Fact]
    public async Task ShutdownCancellationStartsNoRequest()
    {
        using var transport = new Transport();
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", cancel.Token));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RateLimitUsesBoundedRetryAfterWithoutExposingHeaders()
    {
        using var transport = new Transport { Handler = (_, _) => Task.FromResult(new ModFeedResponse(429, Stream.Null, retryAfter: TimeSpan.FromDays(10))) };
        var error = await Assert.ThrowsAsync<ModFeedRequestException>(() => new ModFeedClient(transport).FetchAsync(Url, "author.mod", "stable", default));
        Assert.True(error.RateLimited);
        Assert.Equal(TimeSpan.FromDays(1), error.RetryAfter);
        Assert.Equal("Update request failed.", error.Message);
    }
}

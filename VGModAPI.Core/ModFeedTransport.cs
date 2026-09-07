using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VGModAPI.Core;

internal interface IModFeedTransport : IDisposable
{
    Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken cancellation);
}

internal sealed class ModFeedResponse : IDisposable
{
    internal int Status { get; }
    internal string? Location { get; }
    internal TimeSpan? RetryAfter { get; }
    internal long? Length { get; }
    internal Stream Body { get; }
    private readonly IDisposable? _owner;
    internal ModFeedResponse(int status, Stream body, string? location = null, TimeSpan? retryAfter = null,
        long? length = null, IDisposable? owner = null)
    { Status = status; Body = body; Location = location; RetryAfter = retryAfter; Length = length; _owner = owner; }
    public void Dispose() { try { Body.Dispose(); } finally { _owner?.Dispose(); } }
}

internal sealed class HttpModFeedTransport : IModFeedTransport
{
    private readonly HttpClient _client;
    internal HttpModFeedTransport()
    {
        // Keep platform TLS verification. No proxy, cookies, credentials or automatic redirects.
        _client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, UseDefaultCredentials = false
        }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var retry = response.Headers.RetryAfter;
            return new ModFeedResponse((int)response.StatusCode, body, response.Headers.Location?.OriginalString,
                retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow), response.Content.Headers.ContentLength, response);
        }
        catch { response.Dispose(); throw; }
    }
    public void Dispose() => _client.Dispose();
}

internal sealed class ModFeedRequestException : Exception
{
    internal bool RateLimited { get; }
    internal TimeSpan RetryAfter { get; }
    internal string? Origin { get; }
    internal ModFeedRequestException(bool rateLimited = false, TimeSpan? retryAfter = null, string? origin = null) : base("Update request failed.")
    {
        RateLimited = rateLimited;
        Origin = origin;
        RetryAfter = TimeSpan.FromSeconds(Math.Max(60, Math.Min(86400, (retryAfter ?? TimeSpan.FromMinutes(15)).TotalSeconds)));
    }
}

internal sealed class ModFeedClient
{
    private readonly IModFeedTransport _transport;
    private readonly TimeSpan _deadline;
    private readonly Func<string, TimeSpan?>? _backoff;
    internal ModFeedClient(IModFeedTransport transport, TimeSpan? deadline = null, Func<string, TimeSpan?>? backoff = null)
    { _transport = transport; _deadline = deadline ?? TimeSpan.FromSeconds(10); _backoff = backoff; }

    internal async Task<ModUpdateFeed> FetchAsync(string source, string pluginId, string channel, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(_deadline);
        var current = source;
        for (var hop = 0; ; ++hop)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!ModUpdateHosts.Allowed(current)) throw new FormatException("Unsupported update host or URL.");
            var uri = new Uri(current);
            var delay = _backoff?.Invoke(uri.Host);
            if (delay > TimeSpan.Zero) throw new ModFeedRequestException(true, delay, uri.Host);
            using var response = await _transport.GetAsync(uri, deadline.Token).ConfigureAwait(false);
            using var abort = deadline.Token.Register(() =>
            {
                try { response.Dispose(); }
                catch (Exception) { /* Cancellation cleanup must not throw on the timer thread. */ }
            });
            if (response.Status is 301 or 302 or 303 or 307 or 308)
            {
                if (hop >= 3 || response.Location == null || response.Location.Length > 2048 ||
                    !Uri.TryCreate(uri, response.Location, out var redirect)) throw new FormatException("Invalid or excessive update redirects.");
                current = redirect.AbsoluteUri;
                continue;
            }
            if (response.Status is 429 or 503) throw new ModFeedRequestException(true, response.RetryAfter, uri.Host);
            if (response.Status != 200) throw new ModFeedRequestException();
            if (response.Length > ModMetadataCodec.MaxBytes) throw new FormatException("Update feed is oversized.");
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var count = await response.Body.ReadAsync(buffer, 0,
                    Math.Min(buffer.Length, ModMetadataCodec.MaxBytes + 1 - (int)bytes.Length), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                if (bytes.Length > ModMetadataCodec.MaxBytes) throw new FormatException("Update feed is oversized.");
            }
            return ModUpdateFeed.Parse(bytes.ToArray(), pluginId, channel);
        }
    }
}

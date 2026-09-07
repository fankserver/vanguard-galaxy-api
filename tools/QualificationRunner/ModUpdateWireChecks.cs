using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VGModAPI.Core;

namespace VGModAPI.Qualification;

// Real platform HTTP/TLS and the production redirect/parser path, not a scripted transport.
internal static class ModUpdateWireChecks
{
    private sealed class Observed : IModFeedTransport
    {
        private readonly HttpModFeedTransport _inner = new HttpModFeedTransport();
        internal readonly List<int> Statuses = new List<int>();
        public async Task<ModFeedResponse> GetAsync(Uri uri, CancellationToken token)
        {
            var response = await _inner.GetAsync(uri, token).ConfigureAwait(false);
            Statuses.Add(response.Status); // Sequential requests; no URL/query/token is recorded.
            return response;
        }
        public void Dispose() => _inner.Dispose();
    }
    internal static async Task RunAsync(string revision, Action<string> record)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(revision, "\\A[0-9a-f]{40}\\z"))
            throw new InvalidOperationException("Wire fixtures require an exact committed revision.");
        var root = "https://raw.githubusercontent.com/fankserver/vanguard-galaxy-api/" + revision + "/tools/fixtures/mod-updates/";
        using var transport = new Observed();
        var client = new ModFeedClient(transport);
        foreach (var channel in new[] { "stable", "experimental" })
        {
            var feed = await client.FetchAsync(root + channel + ".json", ModUpdateChecks.Id, channel, CancellationToken.None);
            if (feed.Version != new Version(0, 1, 0, 0) || feed.ReleaseUrl != ModUpdateChecks.Release)
                throw new InvalidOperationException("Wire fixture identity changed.");
            record("wire-platform-tls-parser-" + channel);
        }
        var before = transport.Statuses.Count;
        await client.FetchAsync("https://github.com/fankserver/vanguard-galaxy-api/raw/" + revision + "/tools/fixtures/mod-updates/stable.json",
            ModUpdateChecks.Id, "stable", CancellationToken.None);
        if (transport.Statuses.Count <= before + 1 || transport.Statuses[before] < 300 || transport.Statuses[before] >= 400)
            throw new InvalidOperationException("Wire redirect was not observed.");
        record("wire-https-redirect");
        foreach (var file in new[] { "invalid.json", "oversized.json", "experimental.json" })
        {
            var rejected = false;
            try { await client.FetchAsync(root + file, ModUpdateChecks.Id, "stable", CancellationToken.None); }
            catch (FormatException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Wire invalid fixture accepted: " + file);
        }
        record("wire-invalid-oversized-channel-rejected");
    }
}

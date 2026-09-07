using System;

namespace VGModAPI.Core;

internal sealed class ModUpdateFeed
{
    internal Version Version { get; }
    internal string ReleaseUrl { get; }
    internal ModUpdateFeed(Version version, string releaseUrl) { Version = version; ReleaseUrl = releaseUrl; }

    internal static ModUpdateFeed Parse(byte[] bytes, string pluginId, string channel)
    {
        var fields = ModMetadataCodec.ReadFlat(bytes);
        if (fields.Count != 5 || !fields.TryGetValue("schemaVersion", out var schema) || schema != "1" ||
            !fields.TryGetValue("pluginId", out var id) || id != pluginId || id.Length > 128 ||
            !fields.TryGetValue("channel", out var actualChannel) || actualChannel != channel ||
            actualChannel is not ("stable" or "experimental") ||
            !fields.TryGetValue("version", out var version) || !ModMetadataCodec.TryVersion(version, out var parsed) ||
            !fields.TryGetValue("releaseUrl", out var release) || !ModMetadataCodec.IsPublicHttpsUrl(release))
            throw new FormatException("Invalid update feed schema, identity, channel, version or release URL.");
        return new ModUpdateFeed(parsed!, release);
    }
}

internal static class ModUpdateHosts
{
    // Exact hosts only, including GitHub's signed release-asset redirect destinations.
    // DNS/OS trust remains outside this policy; this is not a network sandbox.
    internal static bool Allowed(string value)
    {
        if (!ModMetadataCodec.IsPublicHttpsUrl(value)) return false;
        var uri = new Uri(value);
        return uri.IdnHost.ToLowerInvariant() is "github.com" or "raw.githubusercontent.com" or
            "objects.githubusercontent.com" or "release-assets.githubusercontent.com";
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

/// <summary>The host-authenticated identity of one loaded plugin. Consumers cannot construct or forge it.</summary>
internal sealed class StoryHostPlugin
{
    internal string PluginId { get; }
    internal string DisplayName { get; }
    internal StoryHostPlugin(string pluginId, string? displayName = null)
    {
        if (string.IsNullOrEmpty(pluginId) || pluginId.Length > 128) throw new ArgumentException("A host plugin identity is 1-128 characters.", nameof(pluginId));
        PluginId = pluginId;
        DisplayName = string.IsNullOrEmpty(displayName) ? pluginId : displayName!;
    }
}

/// <summary>
/// Resolves a caller-supplied plugin instance to the identity the HOST recorded for it. The story
/// module never derives a provider from a caller-supplied string; the host adapter supplies this
/// authenticator, and only it knows which loaded plugin an object belongs to.
/// </summary>
internal delegate StoryHostPlugin? StoryHostAuthenticator(object pluginInstance);

/// <summary>
/// Deterministic mapping from a host plugin identity to the API's provider segment.
///
/// Host plugin IDs are not valid segments (they routinely contain dots, upper case and other
/// characters the identifier charset excludes, and the charset must exclude dots so that
/// <c>vgmodapi.story.&lt;provider&gt;.&lt;local&gt;</c> stays unambiguous). The mapping therefore keeps a
/// readable slug and appends a truncated SHA-256 digest of the EXACT plugin ID, so two different
/// plugin IDs that slug identically still differ. A truncated digest is collision-resistant, not a
/// proof of injectivity, which is why the module additionally records which host plugin owns a
/// segment and refuses a second, different plugin that maps onto it.
/// </summary>
internal static class StoryProviderIdentity
{
    internal const int SlugLength = 24;
    internal const int DigestLength = 10;

    internal static string Segment(StoryHostPlugin plugin)
    {
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        var slug = Slug(plugin.PluginId);
        var segment = slug + "-" + Digest(plugin.PluginId);
        if (!StoryContentId.IsValidSegment(segment)) throw new InvalidOperationException("Derived provider segment is not a valid identity segment.");
        return segment;
    }

    private static string Slug(string pluginId)
    {
        var builder = new StringBuilder(SlugLength);
        foreach (var raw in pluginId)
        {
            var c = char.ToLowerInvariant(raw);
            bool allowed = c is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (!allowed)
            {
                // Collapse every run of unsupported characters into one hyphen; the digest below,
                // not the slug, is what keeps different plugin IDs apart.
                if (builder.Length > 0 && builder[builder.Length - 1] != '-') builder.Append('-');
                continue;
            }
            builder.Append(c);
            if (builder.Length >= SlugLength) break;
        }
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0 || slug[0] is not (>= 'a' and <= 'z')) slug = "p" + slug;
        return slug.Length > SlugLength ? slug.Substring(0, SlugLength).TrimEnd('-') : slug;
    }

    private static string Digest(string pluginId)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(pluginId));
        return string.Concat(hash.Take((DigestLength + 1) / 2).Select(b => b.ToString("x2"))).Substring(0, DigestLength);
    }
}

/// <summary>Why a host plugin could not take a provider segment.</summary>
internal enum StoryBindingStatus { Bound, AlreadyBoundToSelf, Conflict }

/// <summary>
/// Records which host plugin owns each provider segment for the module's lifetime. The binding
/// OUTLIVES a lease: releasing a lease frees the provider's registrations, never its name, so a
/// second plugin can never inherit another mod's content identity. The conflict branch is defensive
/// (<see cref="StoryProviderIdentity"/> derives a digest-qualified segment), but the module refuses
/// rather than trusting a truncated digest to be injective.
/// </summary>
internal sealed class StoryProviderBindings
{
    private readonly Dictionary<string, string> _pluginBySegment = new(StringComparer.Ordinal);

    internal StoryBindingStatus Bind(string segment, string pluginId)
    {
        if (!_pluginBySegment.TryGetValue(segment, out var owner))
        {
            _pluginBySegment.Add(segment, pluginId);
            return StoryBindingStatus.Bound;
        }
        return string.Equals(owner, pluginId, StringComparison.Ordinal)
            ? StoryBindingStatus.AlreadyBoundToSelf
            : StoryBindingStatus.Conflict;
    }

    internal void Clear() => _pluginBySegment.Clear();
}

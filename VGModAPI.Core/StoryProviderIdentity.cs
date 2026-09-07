using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

/// <summary>
/// The host-authenticated identity of one loaded plugin, including the assembly the host loaded it
/// from. Consumers cannot construct or forge it: it is produced only by the host adapter.
/// </summary>
internal sealed class StoryHostPlugin
{
    internal string PluginId { get; }
    /// <summary>The assembly the host recorded for this plugin instance; the caller must match it.</summary>
    internal Assembly Assembly { get; }

    internal StoryHostPlugin(string pluginId, Assembly assembly)
    {
        if (string.IsNullOrEmpty(pluginId) || pluginId.Length > 128) throw new ArgumentException("A host plugin identity is 1-128 characters.", nameof(pluginId));
        PluginId = pluginId;
        Assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
    }
}

/// <summary>
/// Resolves a caller-supplied plugin instance to the identity the HOST recorded for it, given the
/// assembly that actually called the public entry point. The story module never derives a provider
/// from a caller-supplied string, and never from the argument alone: the host adapter supplies this
/// authenticator, and only it knows which loaded plugin an object belongs to.
/// </summary>
internal delegate StoryHostPlugin? StoryHostAuthenticator(object pluginInstance, Assembly callingAssembly);

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
internal enum StoryBindingStatus { Bound, AlreadyBoundToSelf, Conflict, LimitExceeded }

/// <summary>
/// Records which host plugin owns each provider segment for the module's lifetime. The binding
/// OUTLIVES a lease: releasing a lease frees the provider's registrations, never its name, so a
/// second plugin can never inherit another mod's content identity. The conflict branch is defensive
/// (<see cref="StoryProviderIdentity"/> derives a digest-qualified segment), but the module refuses
/// rather than trusting a truncated digest to be injective.
///
/// The count is bounded because each bound provider owns a reserved share of the occurrence ledger
/// (<see cref="StoryLedger.MaxOccurrencesPerProvider"/>): a provider beyond the bound is refused
/// rather than granted a share that would have to come out of an existing provider's history.
/// </summary>
internal sealed class StoryProviderBindings
{
    /// <summary>Bound providers per session-lifetime module. See <see cref="StoryLedger.MaxOccurrencesPerProvider"/>.</summary>
    internal const int MaxProviders = 32;

    private readonly Dictionary<string, string> _pluginBySegment = new(StringComparer.Ordinal);

    internal int Count => _pluginBySegment.Count;

    internal StoryBindingStatus Bind(string segment, string pluginId)
    {
        if (_pluginBySegment.TryGetValue(segment, out var owner))
            return string.Equals(owner, pluginId, StringComparison.Ordinal)
                ? StoryBindingStatus.AlreadyBoundToSelf
                : StoryBindingStatus.Conflict;
        if (_pluginBySegment.Count >= MaxProviders) return StoryBindingStatus.LimitExceeded;
        _pluginBySegment.Add(segment, pluginId);
        return StoryBindingStatus.Bound;
    }

    internal void Clear() => _pluginBySegment.Clear();
}

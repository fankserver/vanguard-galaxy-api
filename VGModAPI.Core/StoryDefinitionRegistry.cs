using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Pure, fail-closed registry of owned story definitions. It records what SHOULD be installed and
/// refuses collisions; installing into the game is the adapter's job, and this type never touches
/// vanilla state. All access is main-thread-only, like every other API surface.
///
/// Fail-closed means: a refused registration changes nothing, and an identifier already known to be
/// taken (vanilla content or another registration) is never overwritten, because vanilla's own
/// <c>StoryMission.Add</c> would silently replace it.
/// </summary>
internal sealed class StoryDefinitionRegistry
{
    private readonly Dictionary<string, StoryMissionDefinition> _byIdentifier = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _providerByIdentifier = new(StringComparer.Ordinal);
    /// <summary>
    /// Identity of each REGISTRATION, not of the definition. Registering the same immutable definition
    /// object twice mints a new entry, so a handle from an earlier registration can be told apart from
    /// the one that holds the identifier now.
    /// </summary>
    private readonly Dictionary<string, long> _entryByIdentifier = new(StringComparer.Ordinal);
    private long _entries;
    internal object Epoch { get; private set; } = new object();

    /// <summary>
    /// Identifiers that already exist in the WORLD (vanilla or foreign). Reserving is not
    /// registering, and a reservation belongs to the session that observed it: it is dropped at a
    /// session boundary by <see cref="ResetWorldReservations"/> and re-evaluated against the next
    /// world, never carried into a save that does not contain that content.
    /// </summary>
    internal void Reserve(IEnumerable<string> identifiers)
    {
        foreach (var identifier in identifiers ?? throw new ArgumentNullException(nameof(identifiers)))
            if (!string.IsNullOrEmpty(identifier)) _reserved.Add(identifier);
    }

    internal int Count => _byIdentifier.Count;
    internal int ReservedCount => _reserved.Count;

    /// <summary>
    /// Drops world reservations at a session boundary. It is deliberately separate from
    /// <see cref="RemoveProvider"/> and <see cref="Clear"/>: releasing a provider's definitions must
    /// never forget that the CURRENT world still owns an identifier.
    /// </summary>
    internal void ResetWorldReservations() => _reserved.Clear();

    internal bool TryGet(StoryContentId id, out StoryMissionDefinition definition)
        => _byIdentifier.TryGetValue(StoryContentPolicy.Identifier(id), out definition!);

    internal bool Contains(StoryContentId id) => _byIdentifier.ContainsKey(StoryContentPolicy.Identifier(id));

    /// <summary>
    /// Attempts to register. The identifier is derived from the owner-scoped identity, so two
    /// providers using the same local ID cannot collide; a duplicate WITHIN one provider is
    /// diagnosed, and an identifier owned by other content is refused.
    /// </summary>
    internal StoryRegistrationStatus TryRegister(StoryContentId id, StoryMissionDefinition definition, out string diagnostic,
        out string identifier, out long entry)
    {
        identifier = "";
        entry = 0;
        // A policy refusal is about the DEFINITION, never about someone else owning the identifier.
        var refusal = StoryContentPolicy.Refuse(id, definition);
        if (refusal != null) { diagnostic = refusal; return StoryRegistrationStatus.InvalidDefinition; }
        identifier = StoryContentPolicy.Identifier(id);
        if (_byIdentifier.ContainsKey(identifier))
        {
            diagnostic = "Provider '" + id.Provider + "' already registered local ID '" + id.LocalId + "'.";
            return StoryRegistrationStatus.DuplicateLocalId;
        }
        if (_reserved.Contains(identifier))
        {
            diagnostic = "Identifier '" + identifier + "' already exists in this world; the API never replaces existing content.";
            return StoryRegistrationStatus.IdentifierInUse;
        }
        if (_byIdentifier.Count >= StoryContentPolicy.MaxDefinitions)
        {
            diagnostic = "The registry holds its maximum of " + StoryContentPolicy.MaxDefinitions + " definitions; nothing was dropped.";
            return StoryRegistrationStatus.LimitExceeded;
        }
        Epoch = new object();
        _byIdentifier.Add(identifier, definition);
        _providerByIdentifier[identifier] = id.Provider;
        entry = ++_entries;
        _entryByIdentifier[identifier] = entry;
        diagnostic = "";
        return StoryRegistrationStatus.Registered;
    }

    /// <summary>Removes a registration made by THIS identity. Another provider's identity can never remove it.</summary>
    internal bool Unregister(StoryContentId id)
    {
        var identifier = StoryContentPolicy.Identifier(id);
        Epoch = new object();
        _providerByIdentifier.Remove(identifier);
        _entryByIdentifier.Remove(identifier);
        return _byIdentifier.Remove(identifier);
    }

    /// <summary>
    /// Removes a registration ONLY if the identifier still belongs to the entry the caller made. A
    /// handle whose registration was already replaced — unregistered and registered again, even with
    /// the same immutable definition object — removes nothing, so releasing a stale handle can never
    /// take down live content.
    /// </summary>
    internal bool RemoveIfMatches(StoryContentId id, long entry)
    {
        var identifier = StoryContentPolicy.Identifier(id);
        if (!_entryByIdentifier.TryGetValue(identifier, out var current) || current != entry) return false;
        Epoch = new object();
        _entryByIdentifier.Remove(identifier);
        _providerByIdentifier.Remove(identifier);
        return _byIdentifier.Remove(identifier);
    }

    /// <summary>Every registered identifier, for a caller that must release what it installed.</summary>
    internal IReadOnlyList<string> Identifiers() => _byIdentifier.Keys.ToArray();

    /// <summary>The identifiers one provider registered, in no particular order.</summary>
    internal IReadOnlyList<string> IdentifiersOf(string provider)
        => _providerByIdentifier.Where(pair => pair.Value == provider).Select(pair => pair.Key).ToArray();

    /// <summary>The registration entry that currently owns an identifier, or 0 when nothing does.</summary>
    internal long EntryOf(StoryContentId id)
        => _entryByIdentifier.TryGetValue(StoryContentPolicy.Identifier(id), out var entry) ? entry : 0;

    /// <summary>Releases every definition of one provider lease. Saved occurrences are untouched.</summary>
    internal void RemoveProvider(string provider)
    {
        Epoch = new object();
        foreach (var pair in _providerByIdentifier.Where(pair => pair.Value == provider).Select(pair => pair.Key).ToArray())
        { _providerByIdentifier.Remove(pair); _byIdentifier.Remove(pair); _entryByIdentifier.Remove(pair); }
    }

    /// <summary>Drops every registered definition. World reservations survive; they are session-scoped, not provider-scoped.</summary>
    internal void Clear() { Epoch = new object(); _byIdentifier.Clear(); _providerByIdentifier.Clear(); _entryByIdentifier.Clear(); }
}

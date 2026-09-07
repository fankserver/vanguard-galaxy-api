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
    internal StoryRegistrationStatus TryRegister(StoryContentId id, StoryMissionDefinition definition, out string diagnostic, out string identifier)
    {
        identifier = "";
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
        _byIdentifier.Add(identifier, definition);
        _providerByIdentifier[identifier] = id.Provider;
        diagnostic = "";
        return StoryRegistrationStatus.Registered;
    }

    /// <summary>Removes a registration made by THIS identity. Another provider's identity can never remove it.</summary>
    internal bool Unregister(StoryContentId id)
    {
        var identifier = StoryContentPolicy.Identifier(id);
        _providerByIdentifier.Remove(identifier);
        return _byIdentifier.Remove(identifier);
    }

    /// <summary>Releases every definition of one provider lease. Saved occurrences are untouched.</summary>
    internal void RemoveProvider(string provider)
    {
        foreach (var pair in _providerByIdentifier.Where(pair => pair.Value == provider).Select(pair => pair.Key).ToArray())
        { _providerByIdentifier.Remove(pair); _byIdentifier.Remove(pair); }
    }

    /// <summary>Drops every registered definition. World reservations survive; they are session-scoped, not provider-scoped.</summary>
    internal void Clear() { _byIdentifier.Clear(); _providerByIdentifier.Clear(); }
}

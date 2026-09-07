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

    /// <summary>Identifiers that already exist in the world (vanilla or foreign). Reserving is not registering.</summary>
    internal void Reserve(IEnumerable<string> identifiers)
    {
        foreach (var identifier in identifiers ?? throw new ArgumentNullException(nameof(identifiers)))
            if (!string.IsNullOrEmpty(identifier)) _reserved.Add(identifier);
    }

    internal int Count => _byIdentifier.Count;
    internal IReadOnlyCollection<string> Identifiers => _byIdentifier.Keys.ToArray();

    internal bool TryGet(StoryContentId id, out StoryMissionDefinition definition)
        => _byIdentifier.TryGetValue(StoryContentPolicy.Identifier(id), out definition!);

    internal bool Contains(StoryContentId id) => _byIdentifier.ContainsKey(StoryContentPolicy.Identifier(id));

    /// <summary>
    /// Attempts to register. The identifier is derived from the owner-scoped identity, so two
    /// providers using the same local ID cannot collide; a duplicate WITHIN one provider is
    /// diagnosed, and an identifier owned by other content is refused.
    /// </summary>
    internal StoryRegistrationStatus TryRegister(StoryMissionDefinition definition, out string diagnostic, out string identifier)
    {
        identifier = "";
        var refusal = StoryContentPolicy.Refuse(definition);
        if (refusal != null) { diagnostic = refusal; return StoryRegistrationStatus.IdentifierInUse; }
        identifier = StoryContentPolicy.Identifier(definition.Id);
        if (_byIdentifier.ContainsKey(identifier))
        {
            diagnostic = "Provider '" + definition.Id.Provider + "' already registered local ID '" + definition.Id.LocalId + "'.";
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
        diagnostic = "";
        return StoryRegistrationStatus.Registered;
    }

    /// <summary>Removes a registration made by THIS identity. Another provider's identity can never remove it.</summary>
    internal bool Unregister(StoryContentId id) => _byIdentifier.Remove(StoryContentPolicy.Identifier(id));

    internal void Clear() => _byIdentifier.Clear();
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI;

/// <summary>One line of a conversation. Speakers are characters, not raw portrait assets.</summary>
public sealed class CharacterLine
{
    /// <summary>Null means the character the player is talking to.</summary>
    public string? Speaker { get; }
    public string Text { get; }
    private CharacterLine(string? speaker, string text)
    {
        Speaker = speaker;
        Text = CharacterText.Check(text, 4096, nameof(text));
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Line text is required.", nameof(text));
    }
    /// <summary>Spoken by the character the player is talking to.</summary>
    public static CharacterLine Self(string text) => new(null, text);
    /// <summary>Spoken by the player's captain.</summary>
    public static CharacterLine Captain(string text) => new(CharacterSpeakers.Captain, text);
    /// <summary>Spoken by the ship AI.</summary>
    public static CharacterLine ShipAi(string text) => new(CharacterSpeakers.ShipAi, text);
    /// <summary>Spoken by another character: a game registry name (the registry resolves factory
    /// names such as "LuminateCommander", not display names) or an introduced character's lookup name.</summary>
    public static CharacterLine By(string characterName, string text)
        => new(CharacterText.Check(characterName, 512, nameof(characterName)), text);
}

internal static class CharacterSpeakers
{
    internal const string Captain = "\u0001captain", ShipAi = "\u0001shipAi";
}

internal static class CharacterText
{
    internal static string Check(string value, int max, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Nonempty text is required.", parameter);
        if (value.Length > max) throw new ArgumentException("Text exceeds its bound.", parameter);
        foreach (var character in value)
            if (char.IsControl(character)) throw new ArgumentException("Control characters are not supported.", parameter);
        return value;
    }
}

/// <summary>One conversation shown when the player talks to a character.</summary>
public sealed class CharacterConversation
{
    public IReadOnlyList<CharacterLine> Lines { get; }
    /// <summary>Runs once when the conversation finishes; use it to advance the story.</summary>
    public Action? Completed { get; }
    public CharacterConversation(IEnumerable<CharacterLine> lines, Action? completed = null)
    {
        var copy = lines?.ToArray() ?? throw new ArgumentNullException(nameof(lines));
        if (copy.Length == 0 || copy.Length > 64) throw new ArgumentException("1-64 lines are required.", nameof(lines));
        if (copy.Any(line => line == null)) throw new ArgumentException("Null line.", nameof(lines));
        Lines = Array.AsReadOnly(copy); Completed = completed;
    }
}

/// <summary>An owner-scoped named character the game's registry does not contain.</summary>
public sealed class StoryCharacterDefinition
{
    public string LocalId { get; }
    public string Name { get; }
    public string Description { get; }
    /// <summary>Optional registry name of a game character whose portrait this character reuses,
    /// such as "QuestgiverHullBlueprints" (the character displayed as Voss). The registry resolves
    /// factory names, not display names; an unknown name simply leaves the portrait unset.</summary>
    public string? PortraitOf { get; }
    public StoryCharacterDefinition(string localId, string name, string description, string? portraitOf = null)
    {
        LocalId = CharacterText.Check(localId, 128, nameof(localId));
        Name = CharacterText.Check(name, 128, nameof(name));
        Description = CharacterText.Check(description, 1024, nameof(description));
        PortraitOf = portraitOf == null ? null : CharacterText.Check(portraitOf, 512, nameof(portraitOf));
    }
}

public interface IStoryCharacterRegistration : IDisposable
{
    /// <summary>
    /// The name the game's character registry resolves for this character. Use it wherever the game
    /// expects a character name, such as a station's persisted character list or as a line speaker.
    /// </summary>
    string LookupName { get; }
}

/// <summary>
/// Introduces named story characters and attaches owner content to game characters. Characters are
/// rebuilt by the game on every lookup; declarations are consulted each time, so content survives
/// save/load and repeated boarding without consumer bookkeeping, and nothing is written to saves.
/// Unrelated lookups stay completely vanilla, as does everything when content is unavailable.
/// </summary>
public interface IStoryCharacterService : IServiceStatus
{
    /// <summary>
    /// Introduces a character the game does not have. The conversation callback is asked for the
    /// current conversation each time the player talks to the character; null shows nothing.
    /// Mission identities in <paramref name="missionHighlights"/> drive the native offer marker.
    /// </summary>
    IStoryCharacterRegistration Introduce(string pluginId, StoryCharacterDefinition definition,
        Func<CharacterConversation?> conversation, IReadOnlyList<string>? missionHighlights = null);
    /// <summary>
    /// Attaches owner content to a character the game owns, without owning that character's
    /// identity. The callback is consulted first each time the player talks to the character;
    /// null falls back to the character's own dialogue.
    /// </summary>
    IDisposable Extend(string pluginId, string characterName, Func<CharacterConversation?> conversation,
        IReadOnlyList<string>? missionHighlights = null);
}

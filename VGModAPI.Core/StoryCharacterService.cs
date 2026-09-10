using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Declarations for introduced and extended characters. The game rebuilds characters on every
/// registry lookup; the adapter consults these declarations at that moment, so content reasserts
/// itself across save/load and repeated boarding without consumer bookkeeping.
/// </summary>
internal sealed class StoryCharacterService : IStoryCharacterService, IDisposable
{
    internal const string LookupPrefix = "vgmodapi.character.v1.";
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Introduction> _introductions = new();
    private readonly List<Extension> _extensions = new();
    private bool _disposed;
    internal StoryCharacterService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("story-characters"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("story-characters", value,
            value ? "Owner-scoped story characters resolve through the game registry." : "Story-character integration unavailable.", reason);
    }

    public IStoryCharacterRegistration Introduce(string pluginId, StoryCharacterDefinition definition,
        Func<CharacterConversation?> conversation, IReadOnlyList<string>? missionHighlights = null)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StoryCharacterService));
        if (definition == null || conversation == null)
            throw new ArgumentNullException(definition == null ? nameof(definition) : nameof(conversation));
        var provider = Identity(pluginId, nameof(pluginId));
        if (definition.PortraitOf != null && definition.PortraitOf.StartsWith(LookupPrefix, StringComparison.Ordinal))
            throw new ArgumentException("Portraits are reused from game characters, not owned ones.", nameof(definition));
        var highlights = Highlights(missionHighlights);
        if (_introductions.Any(entry => !entry.Disposed && entry.Provider == provider && entry.Definition.LocalId == definition.LocalId))
            throw new InvalidOperationException("Duplicate provider character identity.");
        var introduction = new Introduction(this, provider, definition, conversation, highlights);
        _introductions.Add(introduction);
        return introduction;
    }

    public IDisposable Extend(string pluginId, string characterName, Func<CharacterConversation?> conversation,
        IReadOnlyList<string>? missionHighlights = null)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(StoryCharacterService));
        if (conversation == null) throw new ArgumentNullException(nameof(conversation));
        var provider = Identity(pluginId, nameof(pluginId));
        var name = Identity(characterName, nameof(characterName));
        var extension = new Extension(this, provider, name, conversation, Highlights(missionHighlights));
        _extensions.Add(extension);
        return extension;
    }

    private static string Identity(string value, string parameter) => CharacterText.Check(value, 512, parameter);
    private static string[] Highlights(IReadOnlyList<string>? values)
    {
        if (values == null) return Array.Empty<string>();
        if (values.Count > 16) throw new ArgumentException("Too many mission highlights.", nameof(values));
        return values.Select(value => Identity(value, nameof(values))).ToArray();
    }

    internal Introduction? IntroductionFor(string? lookupName)
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable || lookupName == null) return null;
        foreach (var introduction in _introductions)
            if (!introduction.Disposed && introduction.LookupName == lookupName) return introduction;
        return null;
    }

    internal Extension[] ExtensionsFor(string? characterName)
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable || string.IsNullOrEmpty(characterName)) return Array.Empty<Extension>();
        return _extensions.Where(extension => !extension.Disposed && extension.CharacterName == characterName).ToArray();
    }

    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var introduction in _introductions.ToArray()) introduction.Dispose();
        foreach (var extension in _extensions.ToArray()) extension.Dispose();
        _introductions.Clear(); _extensions.Clear();
        _hub.SetCapability("story-characters", false, "Story-character service stopped.", ServiceUnavailableReason.ApiStopped);
    }

    internal sealed class Introduction : IStoryCharacterRegistration
    {
        private readonly StoryCharacterService _owner;
        internal readonly string Provider;
        internal readonly StoryCharacterDefinition Definition;
        internal readonly Func<CharacterConversation?> Conversation;
        internal readonly string[] MissionHighlights;
        internal bool Disposed;
        public string LookupName { get; }
        internal Introduction(StoryCharacterService owner, string provider, StoryCharacterDefinition definition,
            Func<CharacterConversation?> conversation, string[] highlights)
        {
            _owner = owner; Provider = provider; Definition = definition; Conversation = conversation; MissionHighlights = highlights;
            LookupName = LookupPrefix + provider + "." + definition.LocalId;
        }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._introductions.Remove(this);
        }
    }

    internal sealed class Extension : IDisposable
    {
        private readonly StoryCharacterService _owner;
        internal readonly string Provider, CharacterName;
        internal readonly Func<CharacterConversation?> Conversation;
        internal readonly string[] MissionHighlights;
        internal bool Disposed;
        internal Extension(StoryCharacterService owner, string provider, string characterName,
            Func<CharacterConversation?> conversation, string[] highlights)
        { _owner = owner; Provider = provider; CharacterName = characterName; Conversation = conversation; MissionHighlights = highlights; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._extensions.Remove(this);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonContentBindings
{
    internal readonly Func<BoardingHandle, DungeonDefinition, DungeonContentStatus> ValidateAttachment;
    // Bind records identity only; simulation creation is a separate native boundary.
    internal readonly Action<BoardingHandle, DungeonOccurrence> Bind;
    internal readonly Func<DungeonOccurrence, DungeonEventDefinition, DungeonChoiceDefinition, DungeonContentStatus> ValidateChoice;
    internal readonly Action<DungeonOccurrence, DungeonEventDefinition, DungeonChoiceDefinition> ApplyChoice;
    internal DungeonContentBindings(Func<BoardingHandle, DungeonDefinition, DungeonContentStatus> validateAttachment,
        Action<BoardingHandle, DungeonOccurrence> bind,
        Func<DungeonOccurrence, DungeonEventDefinition, DungeonChoiceDefinition, DungeonContentStatus> validateChoice,
        Action<DungeonOccurrence, DungeonEventDefinition, DungeonChoiceDefinition> applyChoice)
    { ValidateAttachment = validateAttachment; Bind = bind; ValidateChoice = validateChoice; ApplyChoice = applyChoice; }
}

internal sealed class DungeonContentService : IDungeonContentService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly DungeonDefinitionRegistry? _definitions;
    private DungeonDefinitionRegistry _registry => _definitions ?? throw new InvalidOperationException("Dungeon definitions unavailable.");
    private readonly IServiceStatus _status;
    private readonly DungeonStateStore? _store;
    private DungeonStateStore _state => _store ?? throw new InvalidOperationException("Dungeon save data unavailable.");
    private readonly DungeonContentBindings? _bindings;
    private DungeonContentBindings _native => _bindings ?? throw new InvalidOperationException("Dungeon bindings unavailable.");
    private readonly Action<string, Exception> _diagnose;
    private readonly Func<bool> _mutationBlocked;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private bool _disposed, _closing;
    private int _callbacks;
    internal DungeonContentService(LifecycleHub hub, DungeonDefinitionRegistry? registry, DungeonStateStore? state,
        DungeonContentBindings? native, Action<string, Exception> diagnose, Func<bool>? mutationBlocked = null)
    {
        _hub = hub; _definitions = registry; _store = state; _bindings = native; _diagnose = diagnose; _mutationBlocked = mutationBlocked ?? (() => false);
        _status = hub.Services.Get("dungeon-content");
        if ((registry == null || state == null || native == null) && Availability.IsAvailable) hub.SetCapability("dungeon-content", false, "Dungeon bindings unavailable.");
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public IDungeonProvider AcquireProvider(string pluginId, ISaveDataRegistration? saveData = null)
    {
        _hub.CheckThread(); if (_disposed || _closing || _definitions == null || _callbacks != 0) throw new InvalidOperationException("Dungeon registration unavailable.");
        _ = new DungeonDefinitionId(pluginId, "provider");
        if (_providers.ContainsKey(pluginId)) throw new InvalidOperationException("Dungeon provider already acquired.");
        var provider = new Provider(this, pluginId, saveData); _providers.Add(pluginId, provider); return provider;
    }
    internal bool PanelBusy { get { _hub.CheckThread(); return MutationBlocked || !_state.MutationAllowed; } }
    internal (object? Occurrence, object? Provider, object? Definition) PanelToken(Guid id)
    {
        _hub.CheckThread(); if (_disposed || _store == null || !Availability.IsAvailable) return (null, null, null);
        var occurrence = _state.Get(id);
        if (occurrence == null) return (null, null, null);
        _providers.TryGetValue(occurrence.DefinitionId.ProviderId, out var provider);
        _registry.TryGet(occurrence.DefinitionId, out var definition);
        return (occurrence, provider, definition);
    }
    internal IReadOnlyList<(string EventId, string EventText, string ChoiceId, string ChoiceText)> PanelChoices(Guid id, string? eventId = null, string? choiceId = null)
    {
        _hub.CheckThread();
        var result = new List<(string, string, string, string)>();
        if (_disposed || MutationBlocked || !_state.MutationAllowed || _state.Get(id) is not { } occurrence ||
            !_providers.TryGetValue(occurrence.DefinitionId.ProviderId, out var provider) || !Live(provider) ||
            !_registry.TryGet(occurrence.DefinitionId, out var definition) || definition.Version != occurrence.Definition.Version) return result;
        foreach (var item in occurrence.Definition.Events)
        {
            if (occurrence.Choices.ContainsKey(item.Id) || (eventId != null && item.Id != eventId)) continue;
            foreach (var choice in item.Choices)
                if ((choiceId == null || choice.Id == choiceId) && _native.ValidateChoice(occurrence, item, choice) == DungeonContentStatus.ChoiceApplied)
                    result.Add((item.Id, item.Text, choice.Id, choice.Text));
        }
        return result.AsReadOnly();
    }
    internal DungeonContentResult ChooseFromPanel(Guid id, string eventId, string choiceId)
    {
        _hub.CheckThread();
        if (MutationBlocked) return Result(DungeonContentStatus.Unavailable);
        var occurrence = _state.Get(id);
        return occurrence != null && _providers.TryGetValue(occurrence.DefinitionId.ProviderId, out var provider)
            ? Choose(provider, id, eventId, choiceId) : Result(DungeonContentStatus.MissingDefinition);
    }
    private bool MutationBlocked => _disposed || _closing || !Availability.IsAvailable || _bindings == null || _store == null || _callbacks != 0 || _mutationBlocked();
    private bool Live(Provider provider) => !_disposed && _providers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, provider);
    private DungeonContentResult Result(DungeonContentStatus status, Guid? id = null) => new(status, status.ToString(), id);
    private DungeonContentResult Attach(Provider provider, string localId, BoardingHandle target)
    {
        _hub.CheckThread(); if (!Live(provider) || MutationBlocked) return Result(DungeonContentStatus.Unavailable);
        if (!_state.MutationAllowed) return Result(DungeonContentStatus.PersistenceUnavailable);
        if (!_registry.TryGet(new(provider.Id, localId), out var definition)) return Result(DungeonContentStatus.MissingDefinition);
        var status = _native.ValidateAttachment(target, definition); if (status != DungeonContentStatus.Attached) return Result(status);
        if (!Live(provider) || MutationBlocked) return Result(DungeonContentStatus.Unavailable);
        var occurrence = new DungeonOccurrence(Guid.NewGuid(), new(provider.Id, localId), definition);
        if (!_state.Add(occurrence)) return Result(DungeonContentStatus.PersistenceUnavailable);
        _native.Bind(target, occurrence);
        return Result(DungeonContentStatus.Attached, occurrence.Id);
    }
    private DungeonContentResult Choose(Provider provider, Guid id, string eventId, string choiceId)
    {
        _hub.CheckThread(); if (!Live(provider) || MutationBlocked) return Result(DungeonContentStatus.Unavailable);
        if (!_state.MutationAllowed) return Result(DungeonContentStatus.PersistenceUnavailable);
        var occurrence = _state.Get(id);
        if (occurrence == null || occurrence.DefinitionId.ProviderId != provider.Id) return Result(DungeonContentStatus.MissingDefinition);
        if (!_registry.TryGet(occurrence.DefinitionId, out var current)) return Result(DungeonContentStatus.MissingDefinition);
        if (current.Version != occurrence.Definition.Version) return Result(DungeonContentStatus.VersionMismatch);
        if (occurrence.Choices.ContainsKey(eventId)) return Result(DungeonContentStatus.AlreadyChosen);
        var item = occurrence.Definition.Events.SingleOrDefault(e => e.Id == eventId);
        var choice = item?.Choices.SingleOrDefault(c => c.Id == choiceId);
        if (item == null || choice == null) return Result(DungeonContentStatus.InvalidChoice);
        var status = _native.ValidateChoice(occurrence, item, choice); if (status != DungeonContentStatus.ChoiceApplied) return Result(status);
        if (provider.Behaviors.TryGetValue(occurrence.DefinitionId.LocalId, out var behavior) && behavior.Allow != null)
        {
            bool allowed;
            _callbacks++;
            try { allowed = behavior.Allow(new(Snapshot(occurrence), eventId, choiceId)); }
            catch (Exception error)
            {
                try { _diagnose(provider.Id, error); } catch (Exception) { /* Diagnostics must not escape a provider callback boundary. */ }
                allowed = false;
            }
            finally { _callbacks--; }
            if (!allowed) return Result(DungeonContentStatus.Vetoed);
            if (!Live(provider) || MutationBlocked || !provider.Behaviors.TryGetValue(occurrence.DefinitionId.LocalId, out var after) || !ReferenceEquals(after, behavior) || !ReferenceEquals(_state.Get(id), occurrence)) return Result(DungeonContentStatus.Unavailable);
            status = _native.ValidateChoice(occurrence, item, choice); if (status != DungeonContentStatus.ChoiceApplied) return Result(status);
        }
        if (!Live(provider) || MutationBlocked) return Result(DungeonContentStatus.Unavailable);
        if (!_state.Choose(id, provider.Id, eventId, choiceId)) return Result(DungeonContentStatus.PersistenceUnavailable);
        // Selected before native effects: a throwing native operation is never retried implicitly.
        _native.ApplyChoice(occurrence, item, choice);
        return Result(DungeonContentStatus.ChoiceApplied, id);
    }
    private static DungeonOccurrenceSnapshot Snapshot(DungeonOccurrence occurrence) => new(occurrence.Id, occurrence.DefinitionId, occurrence.Definition.Version, occurrence.Choices);
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed || _closing) return; _closing = true;
        if (Availability.IsAvailable) _hub.SetCapability("dungeon-content", false, "Dungeon content service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var provider in _providers.Values.ToArray()) provider.Dispose(); _disposed = true;
    }
    private sealed class Behavior : IDisposable
    {
        private readonly Provider _provider;
        private readonly string _id;
        private readonly IDisposable _registration;
        internal readonly Func<DungeonChoiceContext, bool>? Allow;
        internal Behavior(Provider provider, string id, IDisposable registration, Func<DungeonChoiceContext, bool>? allow)
        { _provider = provider; _id = id; _registration = registration; Allow = allow; }
        public void Dispose()
        {
            _provider.Owner._hub.CheckThread();
            if (_provider.Behaviors.TryGetValue(_id, out var current) && ReferenceEquals(this, current)) _provider.Behaviors.Remove(_id);
            _registration.Dispose();
        }
    }
    private sealed class Provider : IDungeonProvider
    {
        internal readonly DungeonContentService Owner;
        internal readonly string Id;
        internal readonly Dictionary<string, Behavior> Behaviors = new(StringComparer.Ordinal);
        private readonly ISaveDataRegistration? _saveData;
        private readonly Dictionary<string, DungeonInstallationEvents.Installation> _installations = new(StringComparer.Ordinal);
        internal Provider(DungeonContentService owner, string id, ISaveDataRegistration? saveData)
        { Owner = owner; Id = id; _saveData = saveData; }
        public IDungeonInstallation GetInstallation(string poiId)
        {
            Owner._hub.CheckThread();
            if (!Owner.Live(this) || Owner._closing) throw new ObjectDisposedException(nameof(IDungeonProvider));
            if (string.IsNullOrWhiteSpace(poiId)) throw new ArgumentException("A persistent POI identity is required.", nameof(poiId));
            if (!_installations.TryGetValue(poiId, out var installation))
            {
                installation = Owner._hub.Installations.Get(Id, poiId, _saveData);
                _installations.Add(poiId, installation);
            }
            return installation;
        }
        public IDisposable Register(string localId, DungeonDefinition definition, Func<DungeonChoiceContext, bool>? allowChoice = null)
        {
            Owner._hub.CheckThread(); if (!Owner.Live(this) || Owner._closing || Owner._callbacks != 0) throw new InvalidOperationException("Dungeon provider unavailable.");
            DungeonDefinitionCodec.Encode(definition);
            var registration = Owner._registry.Register(new(Id, localId), definition);
            var behavior = new Behavior(this, localId, registration, allowChoice); Behaviors.Add(localId, behavior); return behavior;
        }
        public DungeonContentResult Attach(string localId, BoardingHandle target) => Owner.Attach(this, localId, target);
        public DungeonContentResult Choose(Guid occurrenceId, string eventId, string choiceId) => Owner.Choose(this, occurrenceId, eventId, choiceId);
        public IReadOnlyList<DungeonOccurrenceSnapshot> GetOccurrences()
        {
            Owner._hub.CheckThread(); return Owner.Live(this) && Owner.Availability.IsAvailable && Owner._store != null ? Array.AsReadOnly(Owner._state.Entries.Where(e => e.DefinitionId.ProviderId == Id).Select(Snapshot).ToArray()) : Array.Empty<DungeonOccurrenceSnapshot>();
        }
        public void Dispose()
        {
            Owner._hub.CheckThread(); if (!Owner.Live(this)) return;
            foreach (var installation in _installations.Values) installation.Dispose();
            _installations.Clear();
            foreach (var behavior in Behaviors.Values.ToArray()) behavior.Dispose(); Owner._providers.Remove(Id);
        }
    }
}

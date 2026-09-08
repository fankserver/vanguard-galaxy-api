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

internal sealed class DungeonContentService : IDungeonContent, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly DungeonDefinitionRegistry _registry;
    private readonly DungeonStateStore _state;
    private readonly DungeonContentBindings _native;
    private readonly Action<string, Exception> _diagnose;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private bool _disposed;
    private int _callbacks;
    internal DungeonContentService(LifecycleHub hub, DungeonDefinitionRegistry registry, DungeonStateStore state,
        DungeonContentBindings native, Action<string, Exception> diagnose)
    { _hub = hub; _registry = registry; _state = state; _native = native; _diagnose = diagnose; }
    public IDungeonProvider AcquireProvider(string pluginId)
    {
        _hub.CheckThread(); if (_disposed || _callbacks != 0) throw new InvalidOperationException("Dungeon registration unavailable.");
        _ = new DungeonDefinitionId(pluginId, "provider");
        if (_providers.ContainsKey(pluginId)) throw new InvalidOperationException("Dungeon provider already acquired.");
        var provider = new Provider(this, pluginId); _providers.Add(pluginId, provider); return provider;
    }
    private bool Live(Provider provider) => !_disposed && _providers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, provider);
    private DungeonContentResult Result(DungeonContentStatus status, Guid? id = null) => new(status, status.ToString(), id);
    private DungeonContentResult Attach(Provider provider, string localId, BoardingHandle target)
    {
        _hub.CheckThread(); if (!Live(provider) || _callbacks != 0) return Result(DungeonContentStatus.Unavailable);
        if (!_state.MutationAllowed) return Result(DungeonContentStatus.PersistenceUnavailable);
        if (!_registry.TryGet(new(provider.Id, localId), out var definition)) return Result(DungeonContentStatus.MissingDefinition);
        var status = _native.ValidateAttachment(target, definition); if (status != DungeonContentStatus.Attached) return Result(status);
        var occurrence = new DungeonOccurrence(Guid.NewGuid(), new(provider.Id, localId), definition);
        if (!_state.Add(occurrence)) return Result(DungeonContentStatus.PersistenceUnavailable);
        _native.Bind(target, occurrence);
        return Result(DungeonContentStatus.Attached, occurrence.Id);
    }
    private DungeonContentResult Choose(Provider provider, Guid id, string eventId, string choiceId)
    {
        _hub.CheckThread(); if (!Live(provider) || _callbacks != 0) return Result(DungeonContentStatus.Unavailable);
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
            if (!Live(provider) || !provider.Behaviors.TryGetValue(occurrence.DefinitionId.LocalId, out var after) || !ReferenceEquals(after, behavior) || !ReferenceEquals(_state.Get(id), occurrence)) return Result(DungeonContentStatus.Unavailable);
            status = _native.ValidateChoice(occurrence, item, choice); if (status != DungeonContentStatus.ChoiceApplied) return Result(status);
        }
        if (!_state.Choose(id, provider.Id, eventId, choiceId)) return Result(DungeonContentStatus.PersistenceUnavailable);
        // Selected before native effects: a throwing native operation is never retried implicitly.
        _native.ApplyChoice(occurrence, item, choice);
        return Result(DungeonContentStatus.ChoiceApplied, id);
    }
    private static DungeonOccurrenceSnapshot Snapshot(DungeonOccurrence occurrence) => new(occurrence.Id, occurrence.DefinitionId, occurrence.Definition.Version, occurrence.Choices);
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
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
        internal Provider(DungeonContentService owner, string id) { Owner = owner; Id = id; }
        public IDisposable Register(string localId, DungeonDefinition definition, Func<DungeonChoiceContext, bool>? allowChoice = null)
        {
            Owner._hub.CheckThread(); if (!Owner.Live(this) || Owner._callbacks != 0) throw new InvalidOperationException("Dungeon provider unavailable.");
            DungeonDefinitionCodec.Encode(definition);
            var registration = Owner._registry.Register(new(Id, localId), definition);
            var behavior = new Behavior(this, localId, registration, allowChoice); Behaviors.Add(localId, behavior); return behavior;
        }
        public DungeonContentResult Attach(string localId, BoardingHandle target) => Owner.Attach(this, localId, target);
        public DungeonContentResult Choose(Guid occurrenceId, string eventId, string choiceId) => Owner.Choose(this, occurrenceId, eventId, choiceId);
        public IReadOnlyList<DungeonOccurrenceSnapshot> GetOccurrences()
        {
            Owner._hub.CheckThread(); return Owner.Live(this) ? Array.AsReadOnly(Owner._state.Entries.Where(e => e.DefinitionId.ProviderId == Id).Select(Snapshot).ToArray()) : Array.Empty<DungeonOccurrenceSnapshot>();
        }
        public void Dispose()
        {
            Owner._hub.CheckThread(); if (!Owner.Live(this)) return;
            foreach (var behavior in Behaviors.Values.ToArray()) behavior.Dispose(); Owner._providers.Remove(Id);
        }
    }
}

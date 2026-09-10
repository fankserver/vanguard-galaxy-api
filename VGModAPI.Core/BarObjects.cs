using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class BarContentService
{
    private BarGame? _game;
    internal IBars ForGame(IGame game, Guid session)
    { _checkThread(); return _game = new BarGame(this, game, session); }
    internal void Tick() { _checkThread(); _game?.EnsureDeclarations(); }
    private void QueueInteraction(DefinitionRegistration definition, BarInteraction fact)
    {
        if (_game == null || _game.Session != fact.SessionId || !_game.Game.IsActive) return;
        var patron = _game.GetObject(definition);
        definition.Interactions.Publish(_hub, fact.SessionId, definition.Id.Provider, patron,
            () => definition.Live && patron.Game.IsActive && patron.Status == BarPatronStatus.Assigned && fact.IsCurrent(),
            _storage?.Registration, definition.Lease.SaveData);
    }

    internal sealed class DefinitionRegistration : IBarPatronDefinition
    {
        internal readonly BarContentService Owner;
        internal readonly Lease Lease;
        private readonly BarPatronId _id;
        internal readonly GameplayEvent<IBarPatron> Interactions;
        private bool _closed;
        internal DefinitionRegistration(BarContentService owner, Lease lease, string local)
        { Owner = owner; Lease = lease; _id = new BarPatronId(lease.ProviderId, local); Interactions = new(owner._checkThread); }
        public BarPatronId Id { get { Owner._checkThread(); return _id; } }
        internal bool Live => !_closed && Lease.SaveData?.State.Kind != SaveDataStateKind.Disposed && Owner.Active(Lease) && Lease.Registrations.TryGetValue(_id.LocalId, out var current) && ReferenceEquals(current, this);
        public event Action<IBarPatron>? Interacted { add => Interactions.Add(value); remove => Interactions.Remove(value); }
        internal void Close() { _closed = true; Interactions.Dispose(); Owner._game?.Invalidate(this); }
        public void Dispose()
        {
            Owner._checkThread();
            if (!Live) { Close(); return; }
            Lease.Unregister(_id.LocalId);
        }
    }

    private sealed class BarGame : IBars
    {
        internal readonly BarContentService Owner;
        internal readonly Guid Session;
        private readonly IGame _game;
        private readonly Dictionary<DefinitionRegistration, Patron> _patrons = new();
        internal BarGame(BarContentService owner, IGame game, Guid session) { Owner = owner; _game = game; Session = session; }
        public IGame Game { get { Owner._checkThread(); return _game; } }
        public IBarPatron Get(IBarPatronDefinition definition)
        {
            Owner._checkThread();
            if (definition is not DefinitionRegistration registration || !ReferenceEquals(registration.Owner, Owner))
                throw new ArgumentException("Use a definition registered with this API.", nameof(definition));
            return GetObject(registration);
        }
        internal Patron GetObject(DefinitionRegistration definition)
        {
            if (!_patrons.TryGetValue(definition, out var patron))
                _patrons[definition] = patron = new Patron(this, definition);
            return patron;
        }
        internal void Invalidate(DefinitionRegistration definition)
        { if (_patrons.TryGetValue(definition, out var patron)) { patron.Close(); _patrons.Remove(definition); } }
        internal void Close() { foreach (var patron in _patrons.Values) patron.Close(); _patrons.Clear(); }
        internal void EnsureDeclarations()
        {
            if (!Game.IsActive || Owner._disposed || !Owner.Availability.IsAvailable || Owner._storage == null) return;
            foreach (var lease in Owner._leases.Values.ToArray())
                foreach (var definition in lease.Registrations.Values.ToArray())
                {
                    if (!definition.Live) continue;
                    var patron = GetObject(definition);
                    if (patron.Status == BarPatronStatus.Waiting && !patron.Pending) patron.Restore();
                }
            foreach (var pair in _patrons.ToArray())
                if (!pair.Value.Authored.Live) _patrons.Remove(pair.Key);
        }
    }

    private sealed class Patron : IBarPatron
    {
        private readonly BarGame _scope;
        internal readonly DefinitionRegistration Authored;
        internal bool Pending;
        private BarPatronStatus? _publishedStatus;
        private BarResult _last = new(BarStatus.NotRequested);
        private readonly GameplayEvent<IBarPatron> _changed;
        internal Patron(BarGame scope, DefinitionRegistration definition)
        { _scope = scope; Authored = definition; _changed = new(scope.Owner._checkThread); }
        internal void Close() => _changed.Dispose();
        public IGame Game => _scope.Game;
        public IBarPatronDefinition Definition { get { _scope.Owner._checkThread(); return Authored; } }
        public BarResult LastAction { get { _scope.Owner._checkThread(); return _last; } }
        public event Action<IBarPatron>? Changed { add => _changed.Add(value); remove => _changed.Remove(value); }
        public BarPatronStatus Status
        {
            get
            {
                var owner = _scope.Owner; owner._checkThread();
                if (!Game.IsActive) return BarPatronStatus.GameEnded;
                if (!Authored.Live || owner._storage == null || !owner.Availability.IsAvailable) return BarPatronStatus.Unavailable;
                if (!owner._persistence.Read(_scope.Session, out var rows)) return BarPatronStatus.Waiting;
                var state = rows.FirstOrDefault(row => row.Id == Authored.Id);
                if (state == null) owner._transient.TryGetValue(Authored.Id, out state);
                return state != null ? state.Removed ? BarPatronStatus.Removed : BarPatronStatus.Assigned
                    : _last.Status is BarStatus.Unavailable or BarStatus.InvalidDefinition ? BarPatronStatus.Unavailable : BarPatronStatus.Waiting;
            }
        }
        public BarResult Remove() => Schedule(() => Authored.Lease.Remove(_scope.Session, Authored.Id.LocalId));
        public BarResult Restore() => Schedule(() =>
        {
            // Restoring retained presentation does not substitute startup defaults for saved data.
            var owner = _scope.Owner;
            var refusal = owner.Guard(Authored.Lease, _scope.Session);
            if (refusal != null) return refusal;
            owner._persistence.Read(_scope.Session, out var rows);
            var existing = rows.FirstOrDefault(row => row.Id == Authored.Id);
            if (existing != null)
            {
                if (Authored.Lease.Definitions[Authored.Id.LocalId].Retention != BarPatronRetention.Persistent)
                    return new BarResult(BarStatus.InvalidDefinition, "Saved persistent contacts cannot become transient.");
                if (!existing.Removed) return new BarResult(BarStatus.Succeeded);
                var restored = owner._persistence.Put(_scope.Session, Authored.Id.Provider, existing.WithRemoved(false));
                if (restored) owner.Changed();
                return new BarResult(restored ? BarStatus.Succeeded : BarStatus.Unavailable);
            }
            return Authored.Lease.Place(_scope.Session, Authored.Id.LocalId);
        });
        private BarResult Schedule(Func<BarResult> action)
        {
            var owner = _scope.Owner; owner._checkThread();
            if (!Game.IsActive) return _last = new(BarStatus.GameEnded);
            if (!Authored.Live || owner._storage == null) return _last = new(BarStatus.Unavailable);
            var result = new BarResult(BarStatus.Queued)
            { Ended = () => !Game.IsActive ? BarStatus.GameEnded : !Authored.Live ? BarStatus.Unavailable : null };
            Pending = true; _last = result;
            owner._hub.Gameplay.Enqueue(_scope.Session, Authored.Id.Provider, () =>
            {
                try
                {
                    var applied = action(); result.Finish(!Game.IsActive ? BarStatus.GameEnded : applied.Status, applied.Detail);
                }
                catch (Exception error)
                { result.Finish(BarStatus.Unavailable, "The bar action failed."); owner._hub.Gameplay.Report(Authored.Id.Provider, error); }
                finally { Pending = false; }
                // Waiting-state retries are frame-driven; only actual status transitions are events.
                var status = Status;
                if (_publishedStatus == status) return;
                _publishedStatus = status;
                _changed.Publish(owner._hub, _scope.Session, Authored.Id.Provider, this, () => Game.IsActive && Authored.Live, owner._storage.Registration, Authored.Lease.SaveData);
            }, () => Game.IsActive && Authored.Live, owner._storage.Registration, Authored.Lease.SaveData);
            return result;
        }
    }
}

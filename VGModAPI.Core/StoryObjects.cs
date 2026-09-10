using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class StoryContentService
{
    private readonly Dictionary<StoryContentId, Registration> _authoredDefinitions = new();
    private StoryGame? _gameObjects;
    internal IStory ForGame(IGame game, Guid session, LifecycleHub hub)
    {
        CheckThread();
        _gameObjects = new StoryGame(this, game, session, hub);
        return _gameObjects;
    }
    private void PublishMissionChanges() => _gameObjects?.Refresh();

    internal sealed partial class Registration
    {
        internal Lease Owner => _lease;
        internal StoryContentService Service => _service;
        internal bool IsLive => Active && Owner.SaveData?.State.Kind != SaveDataStateKind.Disposed;
        private GameplayEvent<IStoryMission> _accepted = null!, _completed = null!, _failed = null!, _abandoned = null!, _changed = null!;
        private void InitializeEvents()
        {
            _accepted = new(_service.CheckThread); _completed = new(_service.CheckThread);
            _failed = new(_service.CheckThread); _abandoned = new(_service.CheckThread); _changed = new(_service.CheckThread);
        }
        private void CloseEvents()
        { _accepted.Dispose(); _completed.Dispose(); _failed.Dispose(); _abandoned.Dispose(); _changed.Dispose(); }
        public event Action<IStoryMission>? Accepted { add => _accepted.Add(value); remove => _accepted.Remove(value); }
        public event Action<IStoryMission>? Completed { add => _completed.Add(value); remove => _completed.Remove(value); }
        public event Action<IStoryMission>? Failed { add => _failed.Add(value); remove => _failed.Remove(value); }
        public event Action<IStoryMission>? Abandoned { add => _abandoned.Add(value); remove => _abandoned.Remove(value); }
        public event Action<IStoryMission>? Changed { add => _changed.Add(value); remove => _changed.Remove(value); }
        internal void Publish(StoryGame scope, Mission mission, StoryMissionState previous, StoryMissionState current)
        {
            void Send(GameplayEvent<IStoryMission> events) => events.Publish(scope.Hub, scope.Session, Id.Provider, mission,
                () => IsLive && scope.Game.IsActive, _service._persistence, Owner.SaveData);
            Send(_changed);
            if (previous == current) return;
            if (current == StoryMissionState.Active) Send(_accepted);
            else if (current == StoryMissionState.Completed) Send(_completed);
            else if (current == StoryMissionState.Failed) Send(_failed);
            else if (current == StoryMissionState.Abandoned) Send(_abandoned);
        }
    }

    internal sealed class StoryGame : IStory
    {
        internal readonly StoryContentService Service;
        internal readonly LifecycleHub Hub;
        internal readonly Guid Session;
        private readonly IGame _game;
        private readonly Dictionary<Guid, Mission> _missions = new();
        private readonly List<Mission> _offering = new();
        internal bool Executing;
        internal StoryGame(StoryContentService service, IGame game, Guid session, LifecycleHub hub)
        {
            Service = service; _game = game; Session = session; Hub = hub;
            ReconcileDefinitions();
        }
        internal void ReconcileDefinitions()
        {
            if (!Game.IsActive || Service._restoredSession != Session) return;
            foreach (var entry in Service._ledger.Entries)
                if (Service._authoredDefinitions.TryGetValue(entry.Id, out var definition) && definition.IsLive &&
                    (!_missions.TryGetValue(entry.OccurrenceId, out var existing) || !ReferenceEquals(existing.Authored, definition)))
                    _missions[entry.OccurrenceId] = new Mission(this, definition, entry.OccurrenceId);
        }
        public IGame Game { get { Service.CheckThread(); return _game; } }
        private Registration Definition(IStoryDefinition definition)
        {
            if (definition is not Registration owned || !ReferenceEquals(owned.Service, Service))
                throw new ArgumentException("The definition must have been registered with this API.", nameof(definition));
            return owned;
        }
        public IStoryMission Offer(IStoryDefinition definition)
        {
            Service.CheckThread();
            var owned = Definition(definition);
            var mission = new Mission(this, owned, Guid.Empty);
            mission.Offering = mission.Schedule(() =>
            {
                try
                {
                    var result = Service.Offer(owned.Owner, Session, owned.Id.LocalId);
                    if (result.Accepted)
                    { mission.Occurrence = result.OccurrenceId; _missions[result.OccurrenceId] = mission; }
                    return result;
                }
                finally { _offering.Remove(mission); }
            });
            if (mission.Offering.Status == StoryActionStatus.Queued) _offering.Add(mission);
            return mission;
        }
        public StoryMissionQuery GetMissions(IStoryDefinition definition)
        {
            Service.CheckThread(); var owned = Definition(definition);
            var unavailable = !Game.IsActive ? "This game has ended." : !owned.IsLive ? "The definition is no longer registered." : Service.Unavailable();
            if (unavailable != null) return new(false, Array.Empty<IStoryMission>(), unavailable);
            return new(true, Array.AsReadOnly(_missions.Values.Concat(_offering).Where(mission => ReferenceEquals(mission.Authored, owned)).Cast<IStoryMission>().ToArray()));
        }
        internal void Refresh()
        {
            if (Executing || !Game.IsActive) return;
            _offering.RemoveAll(mission => !mission.Authored.IsLive || mission.Offering.Status != StoryActionStatus.Queued);
            foreach (var mission in _missions.Values.ToArray())
            {
                mission.Refresh();
                if (!Service._ledger.TryGet(mission.Occurrence, out _) || !mission.Authored.IsLive) _missions.Remove(mission.Occurrence);
            }
        }
    }

    internal sealed class Mission : IStoryMission
    {
        private readonly StoryGame _scope;
        internal readonly Registration Authored;
        internal Guid Occurrence;
        private StoryActionResult _lastAction;
        internal StoryActionResult Offering;
        private StoryMissionState _published;
        private bool _withdrawn;
        private IReadOnlyDictionary<string, string> _choices = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        private readonly GameplayEvent<IStoryMission> _changed;
        private readonly Dictionary<string, Objective> _objectives = new(StringComparer.Ordinal);
        internal Mission(StoryGame scope, Registration definition, Guid occurrence)
        {
            _scope = scope; Authored = definition; Occurrence = occurrence;
            _lastAction = new(occurrence == Guid.Empty ? StoryActionStatus.Queued : StoryActionStatus.Succeeded);
            Offering = _lastAction;
            _changed = new(scope.Service.CheckThread); _published = ReadState(); CaptureChoices();
        }
        public IGame Game => _scope.Game;
        public IStoryDefinition Definition { get { _scope.Service.CheckThread(); return Authored; } }
        public Guid Id { get { _scope.Service.CheckThread(); return Occurrence; } }
        public StoryActionResult LastAction
        { get { _scope.Service.CheckThread(); return _lastAction; } }
        public StoryMissionState State { get { _scope.Service.CheckThread(); return ReadState(); } }
        public IReadOnlyDictionary<string, string> Choices { get { _scope.Service.CheckThread(); return _choices; } }
        private void CaptureChoices()
        {
            if (Game.IsActive && Authored.IsLive && _scope.Service._ledger.TryGet(Occurrence, out var entry))
                _choices = new ReadOnlyDictionary<string, string>((entry.State == StoryOccurrenceState.Retired ? entry.Choices : entry.PendingChoices)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
        public event Action<IStoryMission>? Changed { add => _changed.Add(value); remove => _changed.Remove(value); }
        private StoryMissionState ReadState()
        {
            if (!Game.IsActive) return StoryMissionState.GameEnded;
            if (!Authored.IsLive) return StoryMissionState.Unavailable;
            if (_withdrawn) return StoryMissionState.Withdrawn;
            if (Occurrence == Guid.Empty) return Offering.Status == StoryActionStatus.Queued ? StoryMissionState.Offering : StoryMissionState.Unavailable;
            if (!_scope.Service._ledger.TryGet(Occurrence, out var entry)) return _published is StoryMissionState.Completed or StoryMissionState.Failed or StoryMissionState.Abandoned ? _published : StoryMissionState.Unavailable;
            return entry.Outcome switch
            {
                StoryOutcome.Completed => StoryMissionState.Completed,
                StoryOutcome.Failed => StoryMissionState.Failed,
                StoryOutcome.Abandoned => StoryMissionState.Abandoned,
                _ => entry.FailureObserved ? StoryMissionState.Failed : entry.State == StoryOccurrenceState.Active ? StoryMissionState.Active : StoryMissionState.Offered
            };
        }
        internal void Refresh(bool force = false)
        {
            var current = ReadState();
            if (!force && current == _published) return;
            CaptureChoices();
            var previous = _published; _published = current;
            _changed.Publish(_scope.Hub, _scope.Session, Authored.Id.Provider, this, () => Game.IsActive && Authored.IsLive, _scope.Service._persistence, Authored.Owner.SaveData);
            Authored.Publish(_scope, this, previous, current);
        }
        internal StoryActionResult Schedule(Func<StoryTransitionResult> action)
        {
            _scope.Service.CheckThread();
            if (!Game.IsActive) return _lastAction = new(StoryActionStatus.GameEnded);
            if (!Authored.IsLive) return _lastAction = new(StoryActionStatus.Unavailable, "The mission definition is no longer registered.");
            var request = new StoryActionResult(StoryActionStatus.Queued, ended: () => !Game.IsActive ? StoryActionStatus.GameEnded :
                !Authored.IsLive ? StoryActionStatus.Unavailable : null);
            _lastAction = request;
            _scope.Hub.Gameplay.Enqueue(_scope.Session, Authored.Id.Provider, () =>
            {
                _scope.Executing = true;
                try
                {
                    var result = action();
                    request.Finish(!Game.IsActive ? StoryActionStatus.GameEnded : result.Accepted ? StoryActionStatus.Succeeded : result.Status == StoryTransitionStatus.Unavailable
                        ? StoryActionStatus.Unavailable : StoryActionStatus.Rejected, result.Detail);
                }
                catch (Exception error)
                { request.Finish(StoryActionStatus.Unavailable, "The story operation failed."); _scope.Hub.Gameplay.Report(Authored.Id.Provider, error); }
                finally { _scope.Executing = false; }
                _lastAction = request;
                Refresh(true);
                _scope.Refresh();
            }, () => Game.IsActive && Authored.IsLive, _scope.Service._persistence, Authored.Owner.SaveData);
            return request;
        }
        public StoryActionResult Activate() => Schedule(() => Authored.Owner.Activate(_scope.Session, Occurrence));
        public StoryActionResult Withdraw() => Schedule(() =>
        {
            var result = Authored.Owner.Withdraw(_scope.Session, Occurrence);
            if (result.Accepted) _withdrawn = true;
            return result;
        });
        public StoryActionResult Fail(IReadOnlyDictionary<string, string>? choices = null)
            => ScheduleChoices(choices, copy => Authored.Owner.Retire(_scope.Session, Occurrence, StoryOutcome.Failed, copy));
        public StoryActionResult Abandon(IReadOnlyDictionary<string, string>? choices = null)
            => ScheduleChoices(choices, copy => Authored.Owner.Retire(_scope.Session, Occurrence, StoryOutcome.Abandoned, copy));
        public StoryActionResult DeclareChoices(IReadOnlyDictionary<string, string> choices)
            => ScheduleChoices(choices ?? throw new ArgumentNullException(nameof(choices)), copy => Authored.Owner.DeclareChoices(_scope.Session, Occurrence, copy!));
        private StoryActionResult ScheduleChoices(IReadOnlyDictionary<string, string>? choices, Func<IReadOnlyDictionary<string, string>?, StoryTransitionResult> action)
        {
            _scope.Service.CheckThread();
            if (!Game.IsActive) return _lastAction = new(StoryActionStatus.GameEnded);
            if (!Authored.IsLive) return _lastAction = new(StoryActionStatus.Unavailable, "The definition is no longer registered.");
            Dictionary<string, string>? copy = null;
            if (choices != null && !TrySnapshotChoices(choices, out copy, out var refusal))
            {
                _lastAction = new(StoryActionStatus.Rejected, refusal);
                Refresh(true); return _lastAction;
            }
            return Schedule(() => action(copy));
        }
        public IStoryObjective GetObjective(string key)
        {
            _scope.Service.CheckThread();
            if (!StoryContentId.IsValidSegment(key)) throw new ArgumentException("An authored objective key is required.", nameof(key));
            if (!_objectives.TryGetValue(key, out var objective)) { objective = new Objective(this, key); _objectives.Add(key, objective); }
            return objective;
        }
        private sealed class Objective : IStoryObjective
        {
            private readonly Mission _mission;
            private readonly string _key;
            internal Objective(Mission mission, string key) { _mission = mission; _key = key; }
            public IStoryMission Mission => _mission;
            public string Key { get { _mission._scope.Service.CheckThread(); return _key; } }
            public StoryObjectiveQuery Snapshot => !_mission.Game.IsActive || _mission.Occurrence == Guid.Empty
                ? new(StoryKnowledge.Unavailable, null, null, null, "The mission is not available in this game.")
                : _mission.Authored.Owner.Query(_mission._scope.Session, new StoryObjectiveId(_mission.Authored.Id, _mission.Occurrence, _key));
            public StoryActionResult SetProgress(int progress) => _mission.Schedule(() => _mission.Occurrence == Guid.Empty
                ? new StoryTransitionResult(StoryTransitionStatus.UnknownOccurrence, Guid.Empty, "The offer was not admitted.")
                : _mission.Authored.Owner.SetProgress(_mission._scope.Session, new StoryObjectiveId(_mission.Authored.Id, _mission.Occurrence, _key), progress));
        }
    }
}

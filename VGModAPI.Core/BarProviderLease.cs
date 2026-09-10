using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class BarContentService
{
    internal sealed class Lease : IBarProvider
    {
        private readonly BarContentService _owner;
        private readonly string _pluginId;
        internal string PluginId => _pluginId;
        internal readonly Dictionary<string, BarPatronDefinition> Definitions = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Action<BarInteraction>> Interactions = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, BarRosterOwnership> Stations = new(StringComparer.Ordinal);
        public string ProviderId { get; }
        internal readonly ISaveDataRegistration? SaveData;
        internal Lease(BarContentService owner, string provider, string pluginId, ISaveDataRegistration? saveData)
        { _owner = owner; ProviderId = provider; _pluginId = pluginId; SaveData = saveData; }
        internal readonly Dictionary<string, DefinitionRegistration> Registrations = new(StringComparer.Ordinal);
        public BarRegistrationResult Register(BarPatronDefinition definition, Action<IBarPatron>? interact = null)
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return new(BarStatus.Unavailable);
            if (definition == null || (definition.Mission.HasValue && definition.Mission.Value.Provider != ProviderId)) return new(BarStatus.InvalidDefinition);
            try { _ = State(definition); }
            catch (ArgumentException error) { return new(BarStatus.InvalidDefinition, detail: error.Message); }
            if (!Definitions.ContainsKey(definition.LocalId) && Definitions.Count >= BarPatronCodec.MaxPerProvider) return new(BarStatus.LimitExceeded);
            var registration = new DefinitionRegistration(_owner, this, definition.LocalId);
            registration.Interacted += interact;
            Registrations.TryGetValue(definition.LocalId, out var previous);
            Definitions[definition.LocalId] = definition;
            Registrations[definition.LocalId] = registration;
            Interactions[definition.LocalId] = fact => _owner.QueueInteraction(registration, fact);
            previous?.Close();
            _owner.Changed();
            return new(BarStatus.Succeeded, registration);
        }
        private BarPatronState State(BarPatronDefinition definition) => new(new BarPatronId(ProviderId, definition.LocalId),
            definition.StationId, definition.Name, definition.Description, definition.Seed, null, null,
            definition.Portrait, definition.IsMale);
        public BarResult Unregister(string localId)
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return new BarResult(BarStatus.Unavailable);
            if (localId == null || !Definitions.Remove(localId)) return new BarResult(BarStatus.NotRegistered);
            if (Registrations.TryGetValue(localId, out var registration)) { Registrations.Remove(localId); registration.Close(); }
            Interactions.Remove(localId);
            _owner._transient.Remove(new BarPatronId(ProviderId, localId));
            _owner.Changed();
            return new BarResult(BarStatus.Succeeded);
        }
        public BarResult ConfigureStation(string stationId, BarRosterOwnership ownership)
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return new BarResult(BarStatus.Unavailable);
            if (!Enum.IsDefined(typeof(BarRosterOwnership), ownership)) return new BarResult(BarStatus.InvalidDefinition);
            try { _ = new BarPatronState(new BarPatronId(ProviderId, "validation"), stationId, "validation", "validation", "validation"); }
            catch (ArgumentException) { return new BarResult(BarStatus.InvalidDefinition); }
            if (ownership == BarRosterOwnership.Exclusive)
            {
                bool allowed;
                try { allowed = _owner._exclusivePermission(_pluginId); } catch { allowed = false; }
                if (!_owner.Active(this)) return new BarResult(BarStatus.Unavailable);
                if (!allowed) return new BarResult(BarStatus.PermissionDenied);
            }
            if (!Stations.ContainsKey(stationId) && Stations.Count >= 32) return new BarResult(BarStatus.LimitExceeded);
            Stations[stationId] = ownership;
            _owner.Changed();
            return new BarResult(BarStatus.Succeeded);
        }
        public BarResult Place(Guid expectedSessionId, string localId)
        {
            var refusal = _owner.Guard(this, expectedSessionId);
            if (refusal != null) return refusal;
            if (localId == null || !Definitions.TryGetValue(localId, out var definition)) return new BarResult(BarStatus.NotRegistered);
            Guid? occurrence = null;
            if (definition.Mission is { } mission)
            {
                occurrence = _owner.ResolveOccurrence?.Invoke(expectedSessionId, mission);
                if (!_owner.Active(this) || _owner.Guard(this, expectedSessionId) != null) return new BarResult(BarStatus.Unavailable);
                if (occurrence == null) return new BarResult(BarStatus.MissionNotReady, "The linked story occurrence is not currently admitted.");
            }
            var state = new BarPatronState(new BarPatronId(ProviderId, localId), definition.StationId, definition.Name, definition.Description,
                definition.Seed, definition.Mission, occurrence, definition.Portrait, definition.IsMale);
            if (definition.Retention == BarPatronRetention.Transient)
            {
                if (!_owner._persistence.Read(expectedSessionId, out var persisted) || persisted.Any(row => row.Id == state.Id))
                    return new BarResult(BarStatus.InvalidDefinition, "A saved patron cannot be replaced by transient presentation.");
                _owner._transient[state.Id] = state;
                _owner.Changed();
                return new BarResult(BarStatus.Succeeded);
            }
            bool placed = _owner._persistence.Put(expectedSessionId, ProviderId, state);
            if (placed) _owner.Changed();
            return new BarResult(placed ? BarStatus.Succeeded : BarStatus.Unavailable);
        }
        public BarResult Remove(Guid expectedSessionId, string localId)
        {
            var refusal = _owner.Guard(this, expectedSessionId);
            if (refusal != null) return refusal;
            BarPatronId id;
            try { id = new BarPatronId(ProviderId, localId); } catch (ArgumentException) { return new BarResult(BarStatus.InvalidDefinition); }
            if (!_owner._persistence.Read(expectedSessionId, out var saved)) return new BarResult(BarStatus.Unavailable);
            var state = saved.FirstOrDefault(row => row.Id == id);
            if (state == null && !_owner._transient.TryGetValue(id, out state))
            {
                if (!Definitions.TryGetValue(localId, out var definition)) return new BarResult(BarStatus.NotRegistered);
                state = State(definition);
            }
            if (state.Removed) return new BarResult(BarStatus.Succeeded);
            bool removed;
            if (!saved.Any(row => row.Id == id) && Definitions.TryGetValue(localId, out var current) && current.Retention == BarPatronRetention.Transient)
            { _owner._transient[id] = state.WithRemoved(true); removed = true; }
            else removed = _owner._persistence.Put(expectedSessionId, ProviderId, state.WithRemoved(true));
            if (removed) _owner.Changed();
            return new BarResult(removed ? BarStatus.Succeeded : BarStatus.Unavailable);
        }

        public void Dispose()
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return;
            _owner._leases.Remove(ProviderId);
            _owner.Changed();
            foreach (var local in Definitions.Keys) _owner._transient.Remove(new BarPatronId(ProviderId, local));
            foreach (var registration in Registrations.Values) registration.Close();
            Registrations.Clear(); Definitions.Clear(); Stations.Clear(); Interactions.Clear();
        }
    }
}

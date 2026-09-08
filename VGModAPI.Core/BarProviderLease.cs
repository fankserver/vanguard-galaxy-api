using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class BarContentService
{
    private sealed class Lease : IBarProvider
    {
        private readonly BarContentService _owner;
        private readonly string _pluginId;
        internal readonly Dictionary<string, BarPatronDefinition> Definitions = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, BarRosterOwnership> Stations = new(StringComparer.Ordinal);
        public string ProviderId { get; }
        internal Lease(BarContentService owner, string provider, string pluginId) { _owner = owner; ProviderId = provider; _pluginId = pluginId; }
        public BarResult Register(BarPatronDefinition definition)
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return new BarResult(BarStatus.Unavailable);
            if (definition == null || (definition.Mission.HasValue && definition.Mission.Value.Provider != ProviderId)) return new BarResult(BarStatus.InvalidDefinition);
            if (Definitions.ContainsKey(definition.LocalId)) return new BarResult(BarStatus.DuplicateLocalId);
            if (Definitions.Count >= BarPatronCodec.MaxPerProvider) return new BarResult(BarStatus.LimitExceeded);
            Definitions.Add(definition.LocalId, definition);
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
            return new BarResult(BarStatus.Succeeded);
        }
        public BarResult Place(Guid expectedSessionId, string localId)
        {
            var refusal = _owner.Guard(this, expectedSessionId);
            if (refusal != null) return refusal;
            if (localId == null || !Definitions.TryGetValue(localId, out var definition)) return new BarResult(BarStatus.NotRegistered);
            var state = new BarPatronState(new BarPatronId(ProviderId, localId), definition.StationId, definition.Name, definition.Description,
                definition.Seed, definition.Mission, definition.Occurrence);
            if (definition.Retention == BarPatronRetention.Transient)
            {
                if (!_owner._persistence.Read(expectedSessionId, out var persisted) || persisted.Any(row => row.Id == state.Id))
                    return new BarResult(BarStatus.InvalidDefinition, "A saved patron cannot be replaced by transient presentation.");
                _owner._transient[state.Id] = state;
                return new BarResult(BarStatus.Succeeded);
            }
            return new BarResult(_owner._persistence.Put(expectedSessionId, ProviderId, state) ? BarStatus.Succeeded : BarStatus.Unavailable);
        }
        public BarResult Remove(Guid expectedSessionId, string localId)
        {
            var refusal = _owner.Guard(this, expectedSessionId);
            if (refusal != null) return refusal;
            BarPatronId id;
            try { id = new BarPatronId(ProviderId, localId); } catch (ArgumentException) { return new BarResult(BarStatus.InvalidDefinition); }
            if (_owner._transient.Remove(id)) return new BarResult(BarStatus.Succeeded);
            return new BarResult(_owner._persistence.Remove(expectedSessionId, ProviderId, id) ? BarStatus.Succeeded : BarStatus.Unavailable);
        }
        public void Dispose()
        {
            _owner._checkThread();
            if (!_owner.Active(this)) return;
            _owner._leases.Remove(ProviderId);
            foreach (var local in Definitions.Keys) _owner._transient.Remove(new BarPatronId(ProviderId, local));
            Definitions.Clear(); Stations.Clear();
        }
    }
}

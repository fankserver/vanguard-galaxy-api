using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

internal sealed class OwnedItemService : IOwnedItemService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly StoryHostAuthenticator _authenticate;
    private readonly IServiceStatus _status;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), OwnedItemIdentity> _definitions = new();
    internal event Action? DefinitionsChanged;
    private bool _disposed, _publishing;
    private readonly Action<OwnedItemIdentity>? _publish;
    internal IEnumerable<OwnedItemIdentity> Definitions => new List<OwnedItemIdentity>(_definitions.Values);
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal OwnedItemService(LifecycleHub hub, StoryHostAuthenticator authenticate, Action<OwnedItemIdentity>? publish = null)
    { _hub = hub; _authenticate = authenticate; _publish = publish; _status = hub.Services.Get("owned-items"); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IOwnedItemProvider? AcquireProvider(object pluginInstance) => Acquire(pluginInstance, Assembly.GetCallingAssembly());
    internal IOwnedItemProvider? Acquire(object instance, Assembly caller)
    {
        _hub.CheckThread(); if (_disposed) return null;
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(instance, caller); } catch { return null; }
        if (_disposed || plugin == null || !ReferenceEquals(plugin.Assembly, caller) || _providers.ContainsKey(plugin.PluginId) || _providers.Count >= 32) return null;
        _ = new ContentDeclaration(plugin.PluginId, "items", PersistentContentKind.Item, ContentPersistenceImpact.ApiDependent);
        var provider = new Provider(this, plugin.PluginId); _providers.Add(plugin.PluginId, provider); return provider;
    }
    internal OwnedItemIdentity? Find(string owner, string local)
    { _hub.CheckThread(); return !_disposed && _definitions.TryGetValue((owner, local), out var value) ? value : null; }
    private sealed class Provider : IOwnedItemProvider
    {
        private readonly OwnedItemService _service;
        public string ProviderId { get; }
        internal Provider(OwnedItemService service, string owner) { _service = service; ProviderId = owner; }
        public OwnedItemStatus Register(OwnedItemDefinition definition)
        {
            _service._hub.CheckThread();
            if (_service._disposed || !_service._providers.TryGetValue(ProviderId, out var active) || !ReferenceEquals(this, active)) return OwnedItemStatus.Rejected;
            if (_service._publishing) return OwnedItemStatus.Rejected;
            if (definition == null) return OwnedItemStatus.InvalidDefinition;
            if (_service._definitions.Count >= 512) return OwnedItemStatus.Rejected;
            try
            {
                var identity = new OwnedItemIdentity(ProviderId, definition);
                var key = (ProviderId, definition.LocalId);
                if (_service._definitions.ContainsKey(key)) return OwnedItemStatus.Duplicate;
                _service._definitions.EnsureCapacity(_service._definitions.Count + 1);
                _service._publishing = true;
                try { _service._publish?.Invoke(identity); }
                finally { _service._publishing = false; }
                if (_service._disposed || !_service._providers.TryGetValue(ProviderId, out active) || !ReferenceEquals(this, active))
                    return OwnedItemStatus.Rejected;
                _service._definitions.Add(key, identity);
                try { _service.DefinitionsChanged?.Invoke(); } catch (Exception error) { _service._hub.ReportSubscriberFailure(ProviderId, error); }
                return OwnedItemStatus.Succeeded;
            }
            catch (ArgumentException) { return OwnedItemStatus.InvalidDefinition; }
            catch (Exception error) { _service._hub.ReportSubscriberFailure(ProviderId, error); return OwnedItemStatus.Unavailable; }
        }
        public void Dispose()
        {
            _service._hub.CheckThread();
            if (!_service._providers.TryGetValue(ProviderId, out var active) || !ReferenceEquals(this, active)) return;
            _service._providers.Remove(ProviderId);
            var keys = new List<(string Owner, string Local)>();
            foreach (var key in _service._definitions.Keys) if (key.Owner == ProviderId) keys.Add(key);
            foreach (var key in keys) _service._definitions.Remove(key);
        }
    }
    public void Dispose() { _hub.CheckThread(); _disposed = true; _providers.Clear(); _definitions.Clear(); }
}

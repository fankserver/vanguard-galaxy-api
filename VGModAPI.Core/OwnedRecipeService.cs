using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

internal sealed class OwnedRecipeService : IOwnedRecipeService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Func<RecipeItemReference, string?> _resolve;
    private readonly Action<OwnedRecipeIdentity> _publish;
    private readonly Action<string> _retire;
    private readonly IServiceStatus _status;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), Entry> _entries = new();
    private bool _busy, _disposed;
    private sealed class Entry
    {
        internal readonly OwnedRecipeDefinition Definition;
        internal OwnedRecipeIdentity? Identity;
        internal Entry(OwnedRecipeDefinition definition) { Definition = definition; }
    }
    internal OwnedRecipeService(LifecycleHub hub, StoryHostAuthenticator authenticate, Func<RecipeItemReference, string?> resolve,
        Action<OwnedRecipeIdentity> publish, Action<string> retire)
    { _hub = hub; _authenticate = authenticate; _resolve = resolve; _publish = publish; _retire = retire; _status = hub.Services.Get("owned-recipes"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IOwnedRecipeProvider? AcquireProvider(object pluginInstance) => Acquire(pluginInstance, Assembly.GetCallingAssembly());
    internal IOwnedRecipeProvider? Acquire(object instance, Assembly caller)
    {
        _hub.CheckThread(); if (_disposed || _busy) return null;
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(instance, caller); } catch { return null; }
        if (_disposed || _busy || plugin == null || !ReferenceEquals(plugin.Assembly, caller) || _providers.ContainsKey(plugin.PluginId) || _providers.Count >= 32) return null;
        var provider = new Provider(this, plugin.PluginId); _providers.Add(plugin.PluginId, provider); return provider;
    }
    internal void Refresh()
    {
        _hub.CheckThread(); if (_disposed || _busy) return;
        foreach (var pair in new List<KeyValuePair<(string, string), Entry>>(_entries)) TryPublish(pair.Key, pair.Value);
    }
    private OwnedRecipeStatus TryPublish((string Owner, string Local) key, Entry entry)
    {
        if (_disposed || _busy) return OwnedRecipeStatus.Rejected;
        _busy = true;
        try
        {
            if (entry.Identity != null) { _publish(entry.Identity); return OwnedRecipeStatus.Succeeded; }
            var d = entry.Definition; var inputs = new (string, int)[d.Ingredients.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                string? id = _resolve(d.Ingredients[i].Item);
                if (id == null) return OwnedRecipeStatus.PendingDependencies;
                inputs[i] = (id, d.Ingredients[i].Count);
            }
            var output = _resolve(d.Result.Item); if (output == null) return OwnedRecipeStatus.PendingDependencies;
            var identity = new OwnedRecipeIdentity(key.Owner, key.Local, d.Revision, d.Name, d.Credits, d.Seconds, inputs, (output, d.Result.Count));
            _publish(identity);
            if (_disposed || !_entries.TryGetValue(key, out var current) || !ReferenceEquals(current, entry)) { _retire(identity.NativeId); return OwnedRecipeStatus.Rejected; }
            entry.Identity = identity; return OwnedRecipeStatus.Succeeded;
        }
        catch (Exception error) { _hub.ReportSubscriberFailure(key.Owner, error); return OwnedRecipeStatus.PendingDependencies; }
        finally { _busy = false; }
    }
    private sealed class Provider : IOwnedRecipeProvider
    {
        private readonly OwnedRecipeService _service;
        public string ProviderId { get; }
        internal Provider(OwnedRecipeService service, string id) { _service = service; ProviderId = id; }
        private bool Active => !_service._disposed && _service._providers.TryGetValue(ProviderId, out var current) && ReferenceEquals(current, this);
        public OwnedRecipeStatus Register(OwnedRecipeDefinition definition)
        {
            _service._hub.CheckThread(); if (!Active || _service._busy || _service._entries.Count >= 256) return OwnedRecipeStatus.Rejected;
            try { OwnedRecipeValidation.Validate(ProviderId, definition); } catch (ArgumentException) { return OwnedRecipeStatus.InvalidDefinition; }
            var key = (ProviderId, definition.LocalId);
            if (_service._entries.ContainsKey(key)) return OwnedRecipeStatus.Duplicate;
            var entry = new Entry(definition); _service._entries.Add(key, entry); return _service.TryPublish(key, entry);
        }
        public RecipeId? Find(string localId)
        {
            _service._hub.CheckThread();
            return Active && _service._entries.TryGetValue((ProviderId, localId), out var entry) && entry.Identity != null
                ? new RecipeId("vanilla", "forge/" + entry.Identity.NativeId) : null;
        }
        public void Dispose()
        {
            _service._hub.CheckThread(); if (!Active) return; _service._providers.Remove(ProviderId);
            foreach (var pair in new List<KeyValuePair<(string, string), Entry>>(_service._entries))
                if (pair.Key.Item1 == ProviderId) { _service._entries.Remove(pair.Key); if (pair.Value.Identity != null) _service._retire(pair.Value.Identity.NativeId); }
        }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        foreach (var provider in new List<Provider>(_providers.Values)) provider.Dispose();
        _disposed = true;
    }
}

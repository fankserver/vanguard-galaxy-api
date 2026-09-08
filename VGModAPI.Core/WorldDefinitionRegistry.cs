using System;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.Core;

/// <summary>Host-authenticated live declarations. Registration neither creates native objects nor overwrites saved instance state.</summary>
internal sealed class WorldDefinitionRegistry : IDisposable
{
    internal sealed class Provider : IDisposable
    {
        private readonly WorldDefinitionRegistry _registry;
        internal string Owner { get; }
        internal Provider(WorldDefinitionRegistry registry, string owner) { _registry = registry; Owner = owner; }
        internal bool Register(WorldCombatDefinition definition) => _registry.Register(this, definition);
        public void Dispose() => _registry.Release(this);
    }
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action _checkThread;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), WorldCombatDefinition> _definitions = new();
    private long _revision;
    private bool _disposed;
    internal long Revision { get { _checkThread(); return _revision; } }
    internal WorldDefinitionRegistry(StoryHostAuthenticator authenticate, Action checkThread)
    { _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate)); _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread)); }

    // A public facade must pass Assembly.GetCallingAssembly from a non-inlined entry point.
    internal Provider? Acquire(object pluginInstance, Assembly caller)
    {
        _checkThread(); if (_disposed) return null;
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, caller); } catch { return null; }
        if (_disposed || plugin == null || !ReferenceEquals(plugin.Assembly, caller)) return null;
        _ = new ContentDeclaration(plugin.PluginId, "definition", PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        if (_providers.ContainsKey(plugin.PluginId) || _providers.Count >= 32) return null;
        var provider = new Provider(this, plugin.PluginId);
        Changed(); _providers.Add(plugin.PluginId, provider); return provider;
    }
    private bool Active(Provider provider) => !_disposed && _providers.TryGetValue(provider.Owner, out var current) && ReferenceEquals(current, provider);
    private bool Register(Provider provider, WorldCombatDefinition definition)
    {
        _checkThread();
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (!Active(provider) || _definitions.Count >= WorldSerializationAssociation.MaxObjects) return false;
        var key = (provider.Owner, definition.LocalId);
        if (_definitions.ContainsKey(key)) return false;
        Changed(); _definitions.Add(key, definition); return true;
    }
    // Revision presence alone is not retained-definition compatibility or native admission.
    internal bool HasLiveRevision(WorldSavedObject saved)
    {
        _checkThread();
        if (saved == null) throw new ArgumentNullException(nameof(saved));
        return !_disposed && _providers.ContainsKey(saved.Identity.Owner) &&
            _definitions.TryGetValue((saved.Identity.Owner, saved.Identity.LocalId), out var definition) && definition.Revision == saved.DefinitionRevision;
    }
    private void Release(Provider provider)
    {
        _checkThread(); if (!Active(provider)) return;
        Changed(); _providers.Remove(provider.Owner);
        var keys = new List<(string Owner, string Local)>();
        foreach (var key in _definitions.Keys) if (key.Owner == provider.Owner) keys.Add(key);
        foreach (var key in keys) _definitions.Remove(key);
    }
    private void Changed() => _revision = checked(_revision + 1);
    public void Dispose()
    {
        _checkThread(); if (_disposed) return;
        Changed(); _disposed = true; _providers.Clear(); _definitions.Clear();
    }
}

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
        internal bool Register(WorldCombatDefinition definition, WorldCombatDefinition? previous = null) => _registry.Register(this, definition, previous);
        public void Dispose() => _registry.Release(this);
    }
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action _checkThread;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), WorldCombatDefinition> _definitions = new();
    private readonly Dictionary<(string Owner, string Local), WorldCombatDefinition> _previous = new();
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
    private bool Register(Provider provider, WorldCombatDefinition definition, WorldCombatDefinition? previous)
    {
        _checkThread();
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (previous != null && (previous.LocalId != definition.LocalId || previous.Revision >= definition.Revision ||
            previous.FactionId != definition.FactionId || previous.Level != definition.Level))
            throw new ArgumentException("Migration must retain local identity, faction and level and advance revision.", nameof(previous));
        if (!Active(provider) || _definitions.Count >= WorldSerializationAssociation.MaxObjects) return false;
        var key = (provider.Owner, definition.LocalId);
        if (_definitions.ContainsKey(key)) return false;
        _definitions.EnsureCapacity(_definitions.Count + 1);
        if (previous != null) _previous.EnsureCapacity(_previous.Count + 1);
        Changed(); _definitions.Add(key, definition);
        if (previous != null) _previous.Add(key, previous);
        return true;
    }
    internal bool TryResolve(Provider provider, string localId, out WorldSavedDefinition? saved)
    {
        _checkThread();
        saved = null;
        if (provider == null || !Active(provider) || !_definitions.TryGetValue((provider.Owner, localId), out var definition)) return false;
        saved = new WorldSavedDefinition(provider.Owner, definition);
        return true;
    }

    internal bool MatchesRetained(WorldSavedDefinition saved)
    {
        _checkThread();
        if (saved == null) throw new ArgumentNullException(nameof(saved));
        var retained = saved.Definition;
        return !_disposed && _providers.ContainsKey(saved.Owner) &&
            _definitions.TryGetValue((saved.Owner, retained.LocalId), out var live) &&
            (Same(live, retained) || (_previous.TryGetValue((saved.Owner, retained.LocalId), out var previous) && Same(previous, retained)));
    }
    private static bool Same(WorldCombatDefinition left, WorldCombatDefinition right) =>
        left.LocalId == right.LocalId && left.Revision == right.Revision && left.Name == right.Name && left.FactionId == right.FactionId && left.Level == right.Level;
    internal WorldSavedDefinition? Effective(WorldSavedDefinition saved)
    {
        _checkThread();
        return MatchesRetained(saved) ? new WorldSavedDefinition(saved.Owner, _definitions[(saved.Owner, saved.Definition.LocalId)]) : null;
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
        foreach (var key in keys) { _definitions.Remove(key); _previous.Remove(key); }
    }
    private void Changed() => _revision = checked(_revision + 1);
    public void Dispose()
    {
        _checkThread(); if (_disposed) return;
        Changed(); _disposed = true; _providers.Clear(); _definitions.Clear(); _previous.Clear();
    }
}

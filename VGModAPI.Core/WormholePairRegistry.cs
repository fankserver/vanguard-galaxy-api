using System;
using System.Collections.Generic;
using System.Reflection;

namespace VGModAPI.Core;

/// <summary>Host-authenticated live authored-system declarations. Registration neither creates native objects nor overwrites saved instances.</summary>
internal sealed class WormholePairRegistry : IDisposable
{
    internal sealed class Provider : IDisposable
    {
        private readonly WormholePairRegistry _registry;
        internal string Owner { get; }
        internal Provider(WormholePairRegistry registry, string owner) { _registry = registry; Owner = owner; }
        internal bool Register(WormholePairDefinition definition, WormholePairDefinition? previous = null)
        {
            var mapped = Map(definition ?? throw new ArgumentNullException(nameof(definition)));
            var prior = previous == null ? null : Map(previous);
            return _registry.Register(this, mapped, prior);
        }
        public void Dispose() => _registry.Release(this);
    }
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action _checkThread;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), WormholePairDeclaration> _definitions = new();
    private readonly Dictionary<(string Owner, string Local), WormholePairDeclaration> _previous = new();
    private long _revision;
    private bool _disposed;
    internal long Revision { get { _checkThread(); return _revision; } }
    internal WormholePairRegistry(StoryHostAuthenticator authenticate, Action checkThread)
    { _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate)); _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread)); }

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
    internal bool Register(Provider provider, WormholePairDefinition definition, WormholePairDefinition? previous = null)
        => Register(provider, Map(definition ?? throw new ArgumentNullException(nameof(definition))), previous == null ? null : Map(previous));
    private static WormholePairDeclaration Map(WormholePairDefinition definition)
        => new(definition.LocalId, definition.Revision, definition.Name, definition.Quiet);
    private bool Register(Provider provider, WormholePairDeclaration definition, WormholePairDeclaration? previous)
    {
        _checkThread();
        if (previous != null && (previous.LocalId != definition.LocalId || previous.Revision >= definition.Revision))
            throw new ArgumentException("Migration must retain local identity and advance revision.", nameof(previous));
        if (!Active(provider) || _definitions.Count >= WorldSerializationAssociation.MaxObjects) return false;
        var key = (provider.Owner, definition.LocalId);
        if (_definitions.ContainsKey(key)) return false;
        _definitions.EnsureCapacity(_definitions.Count + 1);
        if (previous != null) _previous.EnsureCapacity(_previous.Count + 1);
        Changed(); _definitions.Add(key, definition);
        if (previous != null) _previous.Add(key, previous);
        return true;
    }
    internal bool TryResolve(Provider provider, string localId, out WormholePairDeclaration? definition)
    {
        _checkThread();
        definition = null;
        if (provider == null || !Active(provider) || !_definitions.TryGetValue((provider.Owner, localId), out var resolved)) return false;
        definition = resolved;
        return true;
    }
    private bool Active(Provider provider) => !_disposed && _providers.TryGetValue(provider.Owner, out var current) && ReferenceEquals(current, provider);
    /// <summary>Resolves the live definition's revision and, when a previous-revision migration is registered, its previous revision — so retained rows can be migrated up instead of failing with RevisionMismatch.</summary>
    internal bool TryResolveMigration(string owner, string localId, out int liveRevision, out int? previousRevision)
    {
        _checkThread(); liveRevision = 0; previousRevision = null;
        if (_disposed || !_providers.ContainsKey(owner) || !_definitions.TryGetValue((owner, localId), out var definition)) return false;
        liveRevision = definition.Revision;
        if (_previous.TryGetValue((owner, localId), out var previous) && previous.Revision < definition.Revision) previousRevision = previous.Revision;
        return true;
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

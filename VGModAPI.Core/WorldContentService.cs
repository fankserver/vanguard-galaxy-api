using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using VGModAPI.Core.Integration;

namespace VGModAPI.Core;

/// <summary>Authenticated Unity-free declaration/creation facade; runtime qualification remains a separate gate.</summary>
internal sealed class WorldContentService : IWorldApi, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly WorldDefinitionRegistry _definitions;
    private readonly WorldAuthoringGate _authoring;
    private readonly Func<bool> _canAuthor;
    private readonly Action? _providerReleased;
    private bool _disposed;
    internal WorldContentService(LifecycleHub hub, WorldDefinitionRegistry definitions, WorldAuthoringGate authoring, Func<bool> canAuthor, Action? providerReleased = null)
    { _hub = hub; _definitions = definitions; _authoring = authoring; _canAuthor = canAuthor; _providerReleased = providerReleased; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IWorldProvider? AcquireProvider(object pluginInstance)
    {
        _hub.CheckThread(); if (_disposed || _hub.CurrentSession != null) return null;
        var provider = _definitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
        return provider == null || _disposed ? null : new Provider(this, provider);
    }
    private sealed class Provider : IWorldProvider
    {
        private readonly WorldContentService _service;
        private readonly WorldDefinitionRegistry.Provider _provider;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider) { _service = service; _provider = provider; }
        public string ProviderId => _provider.Owner;
        public WorldStatus Register(WorldCombatSiteDefinition definition)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                var native = new WorldCombatDefinition(definition.LocalId, definition.Revision, definition.Name, definition.FactionId, definition.Level);
                if (_service._definitions.TryResolve(_provider, native.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                return _provider.Register(native) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }
        public WorldSiteResult FindPersistentCombatSite(Guid expectedSessionId, WorldSiteReference reference)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new WorldSiteResult(WorldStatus.UnknownProvider);
            if (reference == null || reference.ProviderId != ProviderId) return new WorldSiteResult(WorldStatus.NotRegistered);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new WorldSiteResult(WorldStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId) return new WorldSiteResult(WorldStatus.NotReady);
            try
            {
                var record = _service._authoring.TryFind(_provider, expectedSessionId, reference.LocalId, reference.InstanceId,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed);
                return record == null ? new WorldSiteResult(WorldStatus.NotRegistered) :
                    new WorldSiteResult(WorldStatus.Succeeded, new WorldSiteReference(record.Identity.Owner, record.Identity.LocalId, record.Identity.InstanceId), record.Identity.NativeId);
            }
            catch (ArgumentException) { return new WorldSiteResult(WorldStatus.InvalidDefinition); }
        }
        public WorldSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new WorldSiteResult(WorldStatus.UnknownProvider);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new WorldSiteResult(WorldStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId ||
                _service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new WorldSiteResult(WorldStatus.NotReady);
            if (localId == null || !_service._definitions.TryResolve(_provider, localId, out _)) return new WorldSiteResult(WorldStatus.NotRegistered);
            // Allocate the public result before the native commit; no fallible projection follows creation.
            try
            {
                var identity = new WorldObjectIdentity(new ContentDeclaration(ProviderId, localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), instanceId);
                var success = new WorldSiteResult(WorldStatus.Succeeded, new WorldSiteReference(ProviderId, localId, instanceId), identity.NativeId);
                return _service._authoring.TryCreate(_provider, expectedSessionId, localId, instanceId, systemId, x, y,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed) == null
                    ? new WorldSiteResult(WorldStatus.Rejected) : success;
            }
            catch (ArgumentException) { return new WorldSiteResult(WorldStatus.InvalidDefinition); }
        }
        public void Dispose()
        {
            _service._hub.CheckThread(); if (_disposed) return;
            _provider.Dispose(); _disposed = true;
            _service._providerReleased?.Invoke();
        }
    }
    public void Dispose() { _hub.CheckThread(); _disposed = true; }
}

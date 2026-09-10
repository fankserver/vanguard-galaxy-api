using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Enterability declarations by persistent installation identity. The adapter reconciles declared
/// targets against the current galaxy on a slow cadence, so declarations hold across save/load and
/// player absence, bind late-created installations, and stop cleanly when disposed.
/// </summary>
internal sealed class DungeonAegisService : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Declaration> _declarations = new();
    private bool _disposed;
    internal DungeonAegisService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("dungeon-enterability"); }
    internal ServiceAvailability Availability => _status.Availability;
    internal void SetAvailable(bool value, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("dungeon-enterability", value,
            value ? "Owned boarding targets can stay enterable while their objectives are live." : "Enterability integration unavailable.", reason);
    }
    internal IDisposable Declare(string owner, string poiId)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(DungeonAegisService));
        var declaration = new Declaration(this, owner, poiId);
        _declarations.Add(declaration);
        return declaration;
    }
    /// <summary>Distinct currently declared installation identities.</summary>
    internal string[] DeclaredTargets()
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable) return Array.Empty<string>();
        return _declarations.Where(declaration => !declaration.Disposed)
            .Select(declaration => declaration.PoiId).Distinct(StringComparer.Ordinal).ToArray();
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var declaration in _declarations.ToArray()) declaration.Dispose();
        _declarations.Clear();
        _hub.SetCapability("dungeon-enterability", false, "Enterability service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Declaration : IDisposable
    {
        private readonly DungeonAegisService _owner;
        internal readonly string PoiId;
        internal readonly string Provider;
        internal bool Disposed;
        internal Declaration(DungeonAegisService owner, string provider, string poiId)
        { _owner = owner; Provider = provider; PoiId = poiId; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._declarations.Remove(this);
        }
    }
}

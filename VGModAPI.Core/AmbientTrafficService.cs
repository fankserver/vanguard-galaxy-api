using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal enum AmbientSpawnSite { Station, JumpGate }

/// <summary>
/// Plugin-lifetime quiet-location declarations. Anchors are re-resolved by the adapter at each
/// decorative spawn, so a declaration made before its authored anchor exists binds when the anchor
/// appears and continues to hold across save reloads without any consumer bookkeeping.
/// </summary>
internal sealed class AmbientTrafficService : IAmbientTrafficService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Declaration> _declarations = new();
    private bool _disposed;
    internal AmbientTrafficService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("ambient-traffic"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("ambient-traffic", value,
            value ? "Authored locations can quiet decorative traffic." : "Ambient-traffic integration unavailable.", reason);
    }
    public IDisposable SuppressAtStation(string stationId) => Declare(stationOnly: true, stationId, nameof(stationId));
    public IDisposable SuppressInSystemContaining(string poiId) => Declare(stationOnly: false, poiId, nameof(poiId));
    private Declaration Declare(bool stationOnly, string anchor, string parameter)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(AmbientTrafficService));
        if (string.IsNullOrWhiteSpace(anchor) || anchor.Length > 4096 || anchor.Any(char.IsControl))
            throw new ArgumentException("An authored location identity is required.", parameter);
        var declaration = new Declaration(this, stationOnly, anchor);
        _declarations.Add(declaration);
        return declaration;
    }
    /// <summary>
    /// Decides one decorative spawn. <paramref name="uniqueSystemOf"/> returns the identity of the
    /// single current system containing an anchor, or null when the anchor is missing or ambiguous;
    /// unresolved anchors fail open to vanilla behavior rather than suppressing anywhere else.
    /// </summary>
    internal bool ShouldSuppress(AmbientSpawnSite site, string? sitePoiId, string? siteSystemId, Func<string, string?> uniqueSystemOf)
    {
        _hub.CheckThread();
        if (uniqueSystemOf == null) throw new ArgumentNullException(nameof(uniqueSystemOf));
        if (_disposed || !Availability.IsAvailable || string.IsNullOrEmpty(sitePoiId)) return false;
        foreach (var declaration in _declarations.ToArray())
        {
            if (declaration.Disposed) continue;
            if (declaration.StationOnly)
            {
                if (site == AmbientSpawnSite.Station && declaration.Anchor == sitePoiId) return true;
            }
            else if (!string.IsNullOrEmpty(siteSystemId) && uniqueSystemOf(declaration.Anchor) == siteSystemId) return true;
        }
        return false;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var declaration in _declarations.ToArray()) declaration.Dispose();
        _declarations.Clear();
        _hub.SetCapability("ambient-traffic", false, "Ambient-traffic service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Declaration : IDisposable
    {
        private readonly AmbientTrafficService _owner;
        internal readonly bool StationOnly;
        internal readonly string Anchor;
        internal bool Disposed;
        internal Declaration(AmbientTrafficService owner, bool stationOnly, string anchor)
        { _owner = owner; StationOnly = stationOnly; Anchor = anchor; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._declarations.Remove(this);
        }
    }
}

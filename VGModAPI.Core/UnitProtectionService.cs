using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Plugin-lifetime protection declarations keyed by persistent unit-data identity. The adapter
/// consults declarations at damage time, so protection binds to whichever live instance currently
/// carries the identity — across save/load and re-materialisation — without consumer polling.
/// </summary>
internal sealed class UnitProtectionService : IUnitProtectionService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Declaration> _declarations = new();
    private bool _disposed;
    internal UnitProtectionService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("unit-protection"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("unit-protection", value,
            value ? "Story-critical units can be kept alive." : "Unit-protection integration unavailable.", reason);
    }
    public IDisposable Protect(string unitId)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(UnitProtectionService));
        if (string.IsNullOrWhiteSpace(unitId) || unitId.Length > 4096 || unitId.Any(char.IsControl))
            throw new ArgumentException("A persistent unit identity is required.", nameof(unitId));
        var declaration = new Declaration(this, unitId);
        _declarations.Add(declaration);
        return declaration;
    }
    internal bool IsProtected(string? unitId)
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable || string.IsNullOrEmpty(unitId)) return false;
        foreach (var declaration in _declarations)
            if (!declaration.Disposed && declaration.UnitId == unitId) return true;
        return false;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var declaration in _declarations.ToArray()) declaration.Dispose();
        _declarations.Clear();
        _hub.SetCapability("unit-protection", false, "Unit-protection service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Declaration : IDisposable
    {
        private readonly UnitProtectionService _owner;
        internal readonly string UnitId;
        internal bool Disposed;
        internal Declaration(UnitProtectionService owner, string unitId) { _owner = owner; UnitId = unitId; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._declarations.Remove(this);
        }
    }
}

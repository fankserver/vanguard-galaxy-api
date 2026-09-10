using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

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
    private readonly KeyedDeclarations _keyed = new();
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IDisposable Protect(string unitId, string? key = null)
    {
        _hub.CheckThread();
        KeyedDeclarations.Check(key, nameof(key));
        var scope = Assembly.GetCallingAssembly();
        if (_disposed) throw new ObjectDisposedException(nameof(UnitProtectionService));
        if (string.IsNullOrWhiteSpace(unitId) || unitId.Length > 4096 || unitId.Any(char.IsControl))
            throw new ArgumentException("A persistent unit identity is required.", nameof(unitId));
        var declaration = new Declaration(this, unitId, scope, key);
        _keyed.Replace(scope, key, declaration);
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
        _declarations.Clear(); _keyed.Clear();
        _hub.SetCapability("unit-protection", false, "Unit-protection service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Declaration : IDisposable
    {
        private readonly UnitProtectionService _owner;
        internal readonly string UnitId;
        internal bool Disposed;
        private readonly object _scope; private readonly string? _key;
        internal Declaration(UnitProtectionService owner, string unitId, object scope, string? key)
        { _owner = owner; UnitId = unitId; _scope = scope; _key = key; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            _owner._keyed.Forget(_scope, _key, this);
            Disposed = true; _owner._declarations.Remove(this);
        }
    }
}

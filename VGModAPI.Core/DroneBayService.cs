using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>
/// Drone-bay tuning declarations by persistent unit identity, consulted live at each native launch,
/// replacement roll and complement reconciliation, so disposal takes effect immediately.
/// </summary>
internal sealed class DroneBayService : IDroneBayService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly List<Declaration> _declarations = new();
    private bool _disposed;
    internal DroneBayService(LifecycleHub hub)
    { _hub = hub; _status = hub.Services.Get("drone-bays"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value, ServiceUnavailableReason reason = ServiceUnavailableReason.BindingFailed)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("drone-bays", value,
            value ? "Authored encounters can tune exact drone bays." : "Drone-bay integration unavailable.", reason);
    }
    public IDisposable Tune(string unitId, DroneBayTuning tuning)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(DroneBayService));
        if (tuning == null) throw new ArgumentNullException(nameof(tuning));
        var identity = CharacterText.Check(unitId, 4096, nameof(unitId));
        var declaration = new Declaration(this, identity, tuning);
        _declarations.Add(declaration);
        return declaration;
    }
    /// <summary>Effective aspect values: later live declarations override earlier per aspect.</summary>
    internal DroneBayTuning? EffectiveFor(string? unitId)
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable || string.IsNullOrEmpty(unitId)) return null;
        double? launch = null; IReadOnlyList<string>? replacements = null; int? complement = null;
        foreach (var declaration in _declarations)
        {
            if (declaration.Disposed || declaration.UnitId != unitId) continue;
            launch = declaration.Tuning.LaunchSeconds ?? launch;
            replacements = declaration.Tuning.ReplacementDrones ?? replacements;
            complement = declaration.Tuning.Complement ?? complement;
        }
        return launch == null && replacements == null && complement == null
            ? null : new DroneBayTuning(launch, replacements, complement);
    }
    internal (string UnitId, int Complement)[] ComplementTargets()
    {
        _hub.CheckThread();
        if (_disposed || !Availability.IsAvailable) return Array.Empty<(string, int)>();
        return _declarations.Where(declaration => !declaration.Disposed)
            .Select(declaration => declaration.UnitId).Distinct(StringComparer.Ordinal)
            .Select(unitId => (unitId, EffectiveFor(unitId)?.Complement))
            .Where(target => target.Item2 != null)
            .Select(target => (target.unitId, target.Item2!.Value)).ToArray();
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        foreach (var declaration in _declarations.ToArray()) declaration.Dispose();
        _declarations.Clear();
        _hub.SetCapability("drone-bays", false, "Drone-bay service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Declaration : IDisposable
    {
        private readonly DroneBayService _owner;
        internal readonly string UnitId;
        internal readonly DroneBayTuning Tuning;
        internal bool Disposed;
        internal Declaration(DroneBayService owner, string unitId, DroneBayTuning tuning)
        { _owner = owner; UnitId = unitId; Tuning = tuning; }
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (Disposed) return;
            Disposed = true; _owner._declarations.Remove(this);
        }
    }
}

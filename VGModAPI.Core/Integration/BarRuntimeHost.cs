using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core.Integration;

/// <summary>Connects guarded native hook boundaries to the owning content service.</summary>
internal sealed class BarRuntimeHost : IBarHookHost
{
    private readonly BarContentService _service;
    private readonly BarNativeWorld _world;
    private readonly BarNativeContacts _contacts;
    private readonly BarNativeSerialization _serialization;
    private readonly Func<string, BarRosterPlan?> _plan;
    private readonly Func<bool> _canSerialize, _canMutate;
    private readonly Action _checkThread;
    private readonly Action<Exception> _report;
    private readonly ConditionalWeakTable<object, BarRosterPlan> _applied = new();
    private bool _faulted;

    internal BarRuntimeHost(BarContentService service, BarNativeWorld world, BarNativeContacts contacts,
        BarNativeSerialization serialization, Func<string, BarRosterPlan?> plan, Func<bool> canSerialize,
        Func<bool> canMutate, Action checkThread, Action<Exception> report)
    {
        _service = service; _world = world; _contacts = contacts; _serialization = serialization;
        _plan = plan; _canSerialize = canSerialize; _canMutate = canMutate; _checkThread = checkThread; _report = report;
    }

    public IBarRefreshScope BeginRefresh(object bar)
    {
        _checkThread();
        return new RefreshScope(this, bar, _world.BeginNativeRefresh(bar));
    }

    private sealed class RefreshScope : IBarRefreshScope
    {
        private readonly BarRuntimeHost _host;
        private readonly object _bar;
        private readonly BarNativeWorld.RefreshToken _token;
        private bool _completed;
        internal RefreshScope(BarRuntimeHost host, object bar, BarNativeWorld.RefreshToken token)
        { _host = host; _bar = bar; _token = token; }
        public void Complete(bool originalRan, bool succeeded)
        {
            _host._checkThread();
            if (_completed) return;
            _completed = true;
            _host._world.CompleteNativeRefresh(_token, originalRan, succeeded);
            if (_token.Parent == null && originalRan && succeeded) _host.Reconcile(_bar);
        }
    }

    internal BarRosterApplyStatus Reconcile(object bar)
    {
        _checkThread();
        if (_faulted || !_canMutate()) return BarRosterApplyStatus.Unavailable;
        var station = _world.CurrentStationId(bar);
        if (station == null) return BarRosterApplyStatus.Unavailable;
        var plan = _plan(station);
        if (plan == null)
        {
            var snapshot = _world.Capture(station);
            return snapshot != null && _world.Apply(snapshot, snapshot.VanillaPatrons,
                () => _canMutate() && _plan(station) == null)
                ? BarRosterApplyStatus.Applied : BarRosterApplyStatus.Unavailable;
        }
        var result = BarRosterApplication.Apply(_service, _world, plan);
        if (result != BarRosterApplyStatus.Applied) return result;
        foreach (var contact in _world.CurrentRoster(bar))
        {
            if (!_contacts.TryGet(contact, out var state) || !plan.Patrons.Any(row => ReferenceEquals(row, state))) continue;
            _applied.Remove(contact);
            _applied.Add(contact, plan);
        }
        return result;
    }

    public bool TrySerialize(object bar, out object? result)
    {
        _checkThread();
        return _serialization.TrySerialize(bar, () => !_faulted && _canSerialize(), out result);
    }

    public bool IsOwned(object patron) => _contacts.IsOwned(patron);

    public void Interact(object patron)
    {
        _checkThread();
        if (_faulted || !_applied.TryGetValue(patron, out var plan) || !_contacts.TryGet(patron, out var state)) return;
        var admission = _world.CaptureContact(patron);
        if (admission != null) _service.Interact(plan, state, admission);
    }

    public void Fault(Exception error)
    {
        _faulted = true;
        _report(error);
    }
}

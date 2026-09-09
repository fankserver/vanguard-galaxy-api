using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Pure policy composition. Native mutation belongs exclusively to the adapter.</summary>
internal sealed class BoardingRuleService : IBoardingRuleService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Action<string, Exception> _report;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly List<Registration> _registrations = new();
    private bool _evaluating, _disposed;
    public bool IsEvaluating { get { _hub.CheckThread(); return _evaluating; } }
    internal BoardingRuleService(LifecycleHub hub, Action<string, Exception> report)
    { _hub = hub; _report = report; _status = hub.Services.Get("boarding-rules"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public IBoardingRuleProvider AcquireProvider(string pluginId)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(BoardingRuleService));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin ID required.", nameof(pluginId));
        if (_providers.ContainsKey(pluginId)) throw new InvalidOperationException("Provider already acquired: " + pluginId);
        var provider = new Provider(this, pluginId); _providers.Add(pluginId, provider); return provider;
    }
    private bool Current(Guid session)
    {
        var current = _hub.CurrentSession;
        return !_disposed && Availability.IsAvailable && current?.Id == session && current.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;
    }
    private bool Evaluate(Guid session, Kind kind, BoardingEncounterKind encounter, Action<Registration> invoke)
    {
        _hub.CheckThread();
        if (_evaluating || _hub.IsDispatchingCallbacks || !Current(session)) return false;
        var entries = _registrations.Where(r => r.Kind == kind && (r.Scope == BoardingRuleScope.Both ||
            (r.Scope == BoardingRuleScope.Ships) == (encounter == BoardingEncounterKind.Ship)))
            .OrderByDescending(r => r.Priority).ThenBy(r => r.Provider.Id, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        _evaluating = true;
        try
        {
            foreach (var entry in entries)
            {
                if (!Current(session)) return false;
                if (!entry.Active) continue;
                try { invoke(entry); }
                catch (Exception error) { try { _report(entry.Provider.Id + "/" + entry.Id, error); } catch { } }
            }
            return Current(session);
        }
        finally { _evaluating = false; }
    }
    internal BoardingDisableDecision Disable(BoardingDisableContext context)
    {
        var allow = false; var deny = false;
        if (!Evaluate(context.SessionId, Kind.Disable, BoardingEncounterKind.Ship, entry =>
        {
            var result = ((Func<BoardingDisableContext, BoardingDisableDecision>)entry.Callback)(context);
            if (!Enum.IsDefined(typeof(BoardingDisableDecision), result)) throw new ArgumentOutOfRangeException(nameof(result));
            allow |= result == BoardingDisableDecision.Allow; deny |= result == BoardingDisableDecision.Deny;
        })) return BoardingDisableDecision.Vanilla;
        return deny ? BoardingDisableDecision.Deny : allow ? BoardingDisableDecision.Allow : BoardingDisableDecision.Vanilla;
    }
    internal float? Chance(BoardingDisableContext context)
    {
        float? selected = null; int? priority = null; var conflict = false; string? selectedOwner = null;
        if (!Evaluate(context.SessionId, Kind.Chance, BoardingEncounterKind.Ship, entry =>
        {
            var value = ((Func<BoardingDisableContext, float?>)entry.Callback)(context);
            if (!value.HasValue) return;
            if (float.IsNaN(value.Value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            var owner = entry.Provider.Id + "/" + entry.Id;
            if (!priority.HasValue) { priority = entry.Priority; selected = value; selectedOwner = owner; }
            else if (priority == entry.Priority && selected != value)
            {
                conflict = true;
                try { _report(owner, new InvalidOperationException($"Conflicting boarding probability overrides: {selectedOwner} and {owner} at priority {priority}; preserving vanilla.")); } catch { }
            }
        }) || conflict) return null;
        return selected;
    }
    internal (float Power, float Health) Encounter(BoardingEncounterContext context)
    {
        float power = 1, health = 1;
        if (!Evaluate(context.SessionId, Kind.Encounter, context.Kind, entry =>
        {
            var result = ((Func<BoardingEncounterContext, BoardingEncounterTuning>)entry.Callback)(context)
                ?? throw new ArgumentException("Encounter tuning cannot be null.");
            var nextPower = Product(power, result.DefenderPowerMultiplier);
            var nextHealth = Product(health, result.DefenderHealthMultiplier);
            power = nextPower; health = nextHealth;
        })) return (1, 1);
        return (power, health);
    }
    internal float Integrity(BoardingIntegrityContext context)
    {
        _hub.CheckThread();
        if (context.Cause == BoardingDamageCause.HostDestroyed) return context.Amount;
        float multiplier = 1;
        if (!Evaluate(context.Encounter.SessionId, Kind.Integrity, context.Encounter.Kind, entry =>
        {
            var proposal = ((Func<BoardingIntegrityContext, float>)entry.Callback)(context);
            var next = Product(multiplier, proposal);
            if (float.IsInfinity(context.Amount * next)) throw new ArgumentOutOfRangeException(nameof(proposal));
            multiplier = next;
        })) return context.Amount;
        return context.Amount * multiplier;
    }
    internal bool Scuttle(BoardingEncounterContext context) => Allow(context, Kind.Scuttle);
    internal bool Explosion(BoardingEncounterContext context) => Allow(context, Kind.Explosion);
    private bool Allow(BoardingEncounterContext context, Kind kind)
    {
        var allow = true;
        if (!Evaluate(context.SessionId, kind, context.Kind, entry =>
        {
            // Invoke every contribution; a denial must not suppress later diagnostics/disposal effects.
            var result = ((Func<BoardingEncounterContext, bool>)entry.Callback)(context); allow &= result;
        })) return true;
        return allow;
    }
    private static float Product(float current, float proposal)
    {
        if (float.IsNaN(proposal) || float.IsInfinity(proposal) || proposal < 0 || proposal > 10)
            throw new ArgumentOutOfRangeException(nameof(proposal), "Individual multiplier must be finite and between 0 and 10.");
        var next = current * proposal;
        if (float.IsInfinity(next) || next > 100) throw new ArgumentOutOfRangeException(nameof(proposal), "Combined multiplier exceeds 100.");
        return next;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("boarding-rules", false, "Boarding rule service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var provider in _providers.Values.ToArray()) provider.Dispose();
    }
    private enum Kind { Disable, Chance, Encounter, Integrity, Scuttle, Explosion }
    private sealed class Provider : IBoardingRuleProvider
    {
        private readonly BoardingRuleService _owner;
        internal readonly string Id;
        private bool _disposed;
        internal Provider(BoardingRuleService owner, string id) { _owner = owner; Id = id; }
        private IDisposable Register(string id, BoardingRuleScope scope, Delegate callback, Kind kind, int priority)
        {
            _owner._hub.CheckThread();
            if (_disposed || _owner._disposed) throw new ObjectDisposedException(nameof(Provider));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Local rule ID required.", nameof(id));
            if (!Enum.IsDefined(typeof(BoardingRuleScope), scope)) throw new ArgumentOutOfRangeException(nameof(scope));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (_owner._registrations.Any(r => r.Provider == this && r.Id == id)) throw new InvalidOperationException("Duplicate local rule ID: " + id);
            var registration = new Registration(this, id, scope, callback, kind, priority);
            _owner._registrations.Add(registration); return registration;
        }
        public IDisposable RegisterDisable(string localId, Func<BoardingDisableContext, BoardingDisableDecision> evaluate, int priority = 0) => Register(localId, BoardingRuleScope.Ships, evaluate, Kind.Disable, priority);
        public IDisposable RegisterDisableChance(string localId, Func<BoardingDisableContext, float?> probability, int priority = 0) => Register(localId, BoardingRuleScope.Ships, probability, Kind.Chance, priority);
        public IDisposable RegisterExplosion(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, bool> allow, int priority = 0) => Register(localId, scope, allow, Kind.Explosion, priority);
        public IDisposable RegisterEncounter(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, BoardingEncounterTuning> evaluate, int priority = 0) => Register(localId, scope, evaluate, Kind.Encounter, priority);
        public IDisposable RegisterIntegrity(string localId, BoardingRuleScope scope, Func<BoardingIntegrityContext, float> multiplier, int priority = 0) => Register(localId, scope, multiplier, Kind.Integrity, priority);
        public IDisposable RegisterScuttle(string localId, BoardingRuleScope scope, Func<BoardingEncounterContext, bool> allow, int priority = 0) => Register(localId, scope, allow, Kind.Scuttle, priority);
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (_disposed) return; _disposed = true;
            foreach (var registration in _owner._registrations.Where(r => r.Provider == this).ToArray()) registration.Dispose();
            _owner._providers.Remove(Id);
        }
        internal void Remove(Registration registration) { _owner._hub.CheckThread(); registration.Active = false; _owner._registrations.Remove(registration); }
    }
    private sealed class Registration : IDisposable
    {
        internal readonly Provider Provider;
        internal readonly string Id;
        internal readonly BoardingRuleScope Scope;
        internal readonly Delegate Callback;
        internal readonly Kind Kind;
        internal readonly int Priority;
        internal bool Active = true;
        internal Registration(Provider provider, string id, BoardingRuleScope scope, Delegate callback, Kind kind, int priority)
        { Provider = provider; Id = id; Scope = scope; Callback = callback; Kind = kind; Priority = priority; }
        public void Dispose() => Provider.Remove(this);
    }
}

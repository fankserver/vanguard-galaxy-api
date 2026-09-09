using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class BoardingCombatService : IBoardingCombatService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Action<string, Exception> _report;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly List<Registration> _entries = new();
    private bool _evaluating, _disposed;
    internal BoardingCombatService(LifecycleHub hub, Action<string, Exception> report) { _hub = hub; _report = report; _status = hub.Services.Get("boarding-combat"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public bool IsEvaluating { get { _hub.CheckThread(); return _evaluating; } }
    public IBoardingCombatProvider AcquireProvider(string pluginId)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(BoardingCombatService));
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin ID required.", nameof(pluginId));
        if (_providers.ContainsKey(pluginId)) throw new InvalidOperationException("Combat provider already acquired.");
        var provider = new Provider(this, pluginId); _providers.Add(pluginId, provider); return provider;
    }
    private bool Current(Guid session)
    {
        var current = _hub.CurrentSession;
        return !_disposed && Availability.IsAvailable && current?.Id == session && current.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;
    }
    private bool Evaluate(BoardingCombatContext context, bool veto, Action<Registration> apply)
    {
        _hub.CheckThread();
        if (_evaluating || _hub.IsDispatchingCallbacks || !Current(context.Encounter.SessionId)) return false;
        var entries = _entries.Where(e => e.Kind == context.Kind && e.Veto == veto &&
            (e.Scope == BoardingRuleScope.Both || (e.Scope == BoardingRuleScope.Ships) == (context.Encounter.Kind == BoardingEncounterKind.Ship)))
            .OrderByDescending(e => e.Priority).ThenBy(e => e.Provider.Id, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        _evaluating = true;
        try
        {
            foreach (var entry in entries)
            {
                if (!Current(context.Encounter.SessionId)) return false;
                if (!entry.Active) continue;
                try { apply(entry); }
                catch (Exception error) { try { _report(entry.Provider.Id + "/" + entry.Id, error); } catch { } }
            }
            return Current(context.Encounter.SessionId);
        }
        finally { _evaluating = false; }
    }
    internal float Scale(BoardingCombatContext context)
    {
        float multiplier = 1;
        if (!Evaluate(context, false, entry =>
        {
            var factor = ((Func<BoardingCombatContext, float>)entry.Callback)(context);
            if (float.IsNaN(factor) || float.IsInfinity(factor) || factor < 0 || factor > 10) throw new ArgumentOutOfRangeException(nameof(factor));
            var next = multiplier * factor;
            if (next > 100 || (double)context.Value * next > float.MaxValue) throw new ArgumentOutOfRangeException(nameof(factor));
            multiplier = next;
        })) return context.Value;
        return context.Value * multiplier;
    }
    internal bool Allow(BoardingCombatContext context)
    {
        var allowed = true;
        if (!Evaluate(context, true, entry => { var value = ((Func<BoardingCombatContext, bool>)entry.Callback)(context); allowed &= value; })) return true;
        return allowed;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("boarding-combat", false, "Combat service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var provider in _providers.Values.ToArray()) provider.Dispose();
    }
    private sealed class Provider : IBoardingCombatProvider
    {
        private readonly BoardingCombatService _owner;
        internal readonly string Id;
        private bool _disposed;
        internal Provider(BoardingCombatService owner, string id) { _owner = owner; Id = id; }
        private IDisposable Register(string id, BoardingRuleScope scope, BoardingCombatPolicyKind kind, Delegate callback, int priority, bool veto)
        {
            _owner._hub.CheckThread();
            if (_disposed || _owner._disposed) throw new ObjectDisposedException(nameof(Provider));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Local ID required.", nameof(id));
            if (!Enum.IsDefined(typeof(BoardingRuleScope), scope) || !Enum.IsDefined(typeof(BoardingCombatPolicyKind), kind)) throw new ArgumentException("Invalid combat policy.");
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            var numeric = kind is BoardingCombatPolicyKind.Power or BoardingCombatPolicyKind.InitialHealth or BoardingCombatPolicyKind.Morale or BoardingCombatPolicyKind.CasualtyRate;
            if (veto == numeric) throw new ArgumentException("Numeric families require multipliers; discrete effect families require vetoes.");
            if (_owner._entries.Any(e => e.Provider == this && e.Id == id)) throw new InvalidOperationException("Duplicate local combat rule ID.");
            var entry = new Registration(this, id, scope, kind, callback, priority, veto); _owner._entries.Add(entry); return entry;
        }
        public IDisposable RegisterMultiplier(string localId, BoardingRuleScope scope, BoardingCombatPolicyKind kind, Func<BoardingCombatContext, float> multiplier, int priority = 0)
            => Register(localId, scope, kind, multiplier, priority, false);
        public IDisposable RegisterVeto(string localId, BoardingRuleScope scope, BoardingCombatPolicyKind kind, Func<BoardingCombatContext, bool> allow, int priority = 0)
            => Register(localId, scope, kind, allow, priority, true);
        public void Dispose()
        {
            _owner._hub.CheckThread(); if (_disposed) return; _disposed = true;
            foreach (var entry in _owner._entries.Where(e => e.Provider == this).ToArray()) entry.Dispose();
            _owner._providers.Remove(Id);
        }
        internal void Remove(Registration entry) { _owner._hub.CheckThread(); entry.Active = false; _owner._entries.Remove(entry); }
    }
    private sealed class Registration : IDisposable
    {
        internal readonly Provider Provider;
        internal readonly string Id;
        internal readonly BoardingRuleScope Scope;
        internal readonly BoardingCombatPolicyKind Kind;
        internal readonly Delegate Callback;
        internal readonly int Priority;
        internal readonly bool Veto;
        internal bool Active = true;
        internal Registration(Provider provider, string id, BoardingRuleScope scope, BoardingCombatPolicyKind kind, Delegate callback, int priority, bool veto)
        { Provider = provider; Id = id; Scope = scope; Kind = kind; Callback = callback; Priority = priority; Veto = veto; }
        public void Dispose() => Provider.Remove(this);
    }
}

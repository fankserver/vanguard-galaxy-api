using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;

namespace VGModAPI.Runtime;

using Core;

internal sealed class BoardingRuleAdapter : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly BoardingRuleService _rules;
    private readonly Func<string, object, object?> _read;
    private readonly Action<string, object, float> _write;
    private readonly Func<object, bool> _eligible, _live;
    private readonly Action<object, object, float, float> _convert;
    private readonly Action<Exception> _fault;
    private readonly Func<float> _random;
    private readonly List<Scope> _scopes = new();
    private readonly ConditionalWeakTable<object, object> _tuned = new();
    private bool _stopped;
    internal BoardingRuleAdapter(LifecycleHub hub, BoardingRuleService rules, GameBindings game, Func<object, bool> live, Action<Exception> fault, Func<float> random)
    {
        _hub = hub; _rules = rules; _live = live; _fault = fault; _random = random;
        var fields = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var spec in BoardingRuleBindings.Members)
        {
            var type = game.Assembly.GetType(spec.Type, true)!;
            MemberInfo? member = type.GetField(spec.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            member ??= type.GetProperty(spec.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var valueType = member is FieldInfo f ? f.FieldType : (member as PropertyInfo)?.PropertyType;
            if (valueType == null || !NativeTypeName.Matches(valueType, spec.ValueType) ||
                member is PropertyInfo p && (p.GetMethod == null || p.GetMethod.IsStatic || p.GetIndexParameters().Length != 0))
                throw new MissingMemberException(spec.Type, spec.Name);
            if (spec.Key is "power" or "health" && (member is not FieldInfo writable || writable.IsInitOnly))
                throw new MissingFieldException(spec.Type, spec.Name);
            fields.Add(spec.Key, member!);
        }
        _read = (key, obj) => fields[key] is FieldInfo f ? f.GetValue(obj) : ((PropertyInfo)fields[key]).GetValue(obj);
        _write = (key, obj, value) => ((FieldInfo)fields[key]).SetValue(obj, value);
        var calls = game.Resolve(BoardingRuleBindings.Calls);
        _eligible = obj => (bool)Invoke(calls["eligible"], obj, Array.Empty<object>())!;
        _convert = (obj, damage, hullFraction, empFraction) => Invoke(calls["convert"], obj, new object[] { damage, 1f, hullFraction, empFraction });
    }
    internal BoardingRuleAdapter(LifecycleHub hub, BoardingRuleService rules, Func<string, object, object?> read,
        Action<string, object, float> write, Func<object, bool> eligible, Func<object, bool> live,
        Action<object, object, float, float> convert, Action<Exception> fault, Func<float>? random = null)
    { _hub = hub; _rules = rules; _read = read; _write = write; _eligible = eligible; _live = live; _convert = convert; _fault = fault; _random = random ?? (() => throw new InvalidOperationException("Random source required for probability rules.")); }
    private static object? Invoke(MethodInfo method, object obj, object[] args)
    {
        try { return method.Invoke(obj, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private Guid? Session
    {
        get
        {
            var session = _hub.CurrentSession;
            return !_stopped && session?.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized ? session.Id : null;
        }
    }
    private bool Try(Action action)
    {
        if (_stopped) return false;
        try { _hub.CheckThread(); if (!Session.HasValue) return false; action(); return true; }
        catch (Exception error)
        {
            _stopped = true;
            try { _hub.SetCapability("boarding-rules", false, "Boarding rule adapter stopped: " + error.GetType().Name); } catch { }
            try { _fault(error); } catch { }
            return false;
        }
    }
    internal BoardingDisableDecision PrepareDisable(object native, object damage, out DisablePlan? plan)
    {
        DisablePlan? prepared = null;
        Try(() =>
        {
            if (!_live(native) || (bool)_read("destroyed", native)! || !_eligible(native)) return;
            var context = new BoardingDisableContext(Session!.Value, (float)_read("hull", native)!, (float)_read("maxHull", native)!,
                (float)_read("emp", _read("unitData", native)!)!);
            var decision = _rules.Disable(context);
            if (decision == BoardingDisableDecision.Vanilla)
            {
                var chance = _rules.Chance(context);
                if (chance.HasValue && Session == context.SessionId)
                    decision = chance == 1 || (chance > 0 && _random() < chance) ? BoardingDisableDecision.Allow : BoardingDisableDecision.Deny;
            }
            if (Session != context.SessionId || !_live(native) || (bool)_read("destroyed", native)! || !_eligible(native)) return;
            prepared = new DisablePlan(native, damage, context, decision);
        });
        plan = prepared; return prepared?.Decision ?? BoardingDisableDecision.Vanilla;
    }
    /// <summary>Native side effects are deliberately outside adapter-fault containment: never retry conversion after a partial native exception.</summary>
    internal void Convert(DisablePlan plan) => _convert(plan.Native, plan.Damage, plan.Context.HullFraction, Math.Min(1, plan.Context.EmpCharge / plan.Context.MaximumHull));
    internal object? BeginCreation(object native, bool walk)
    {
        Scope? scope = null;
        Try(() => { var location = walk ? _read("location", native)! : native; scope = new Scope(ContextFromLocation(location), null, null, false); _scopes.Add(scope); });
        return scope;
    }
    internal object? BeginEstimate(object location)
    {
        Scope? scope = null;
        Try(() => { scope = new Scope(ContextFromLocation(location), null, null, true); _scopes.Add(scope); });
        return scope;
    }
    private BoardingEncounterContext ContextFromLocation(object location) => new(Session!.Value,
        (bool)_read("locationKind", location)! ? BoardingEncounterKind.Ship : BoardingEncounterKind.Installation, (int)_read("locationLevel", location)!);
    private BoardingEncounterContext ContextFromSimulation(object simulation) => new(Session!.Value,
        _read("kind", simulation)!.ToString() == "HostileShip" ? BoardingEncounterKind.Ship : BoardingEncounterKind.Installation, (int)_read("level", simulation)!);
    internal void EndScope(object? state)
    {
        if (state is Scope scope) _scopes.Remove(scope);
    }
    internal void ApplyScaling(object simulation)
    {
        Try(() =>
        {
            var scope = _scopes.LastOrDefault(s => s.Cause == null && !s.Estimate && s.Context.SessionId == Session);
            if (scope == null || _tuned.TryGetValue(simulation, out _)) return;
            var tuning = _rules.Encounter(scope.Context);
            if (Session != scope.Context.SessionId) return;
            var oldPower = (float)_read("power", simulation)!; var oldHealth = (float)_read("health", simulation)!;
            var power = Multiply(oldPower, tuning.Power); var health = Multiply(oldHealth, tuning.Health);
            try { _write("power", simulation, power); _write("health", simulation, health); }
            catch { _write("power", simulation, oldPower); _write("health", simulation, oldHealth); throw; }
            _tuned.Add(simulation, new object());
        });
    }
    internal float EstimatePower(float defenderPower)
    {
        var result = defenderPower;
        Try(() =>
        {
            var scope = _scopes.LastOrDefault(s => s.Estimate && s.Context.SessionId == Session);
            if (scope == null) return;
            var tuning = _rules.Encounter(scope.Context);
            if (Session == scope.Context.SessionId) result = Multiply(Multiply(defenderPower, tuning.Power), tuning.Health);
        });
        return result;
    }
    internal object? BeginCause(object simulation, BoardingDamageCause cause)
    {
        Scope? scope = null;
        Try(() => { scope = new Scope(ContextFromSimulation(simulation), simulation, cause, false); _scopes.Add(scope); });
        return scope;
    }
    internal bool AllowScuttle(object simulation) => AllowOutcome(simulation, false);
    internal bool AllowExplosion(object simulation) => AllowOutcome(simulation, true);
    private bool AllowOutcome(object simulation, bool explosion)
    {
        var allow = true;
        Try(() => { var context = ContextFromSimulation(simulation); allow = explosion ? _rules.Explosion(context) : _rules.Scuttle(context); if (Session != context.SessionId) allow = true; });
        return allow;
    }
    internal float Damage(object simulation, float amount)
    {
        var result = amount;
        Try(() =>
        {
            var scopes = _scopes.Where(s => ReferenceEquals(s.Native, simulation) && s.Context.SessionId == Session).ToArray();
            var cause = scopes.Any(s => s.Cause == BoardingDamageCause.HostDestroyed) ? BoardingDamageCause.HostDestroyed : scopes.LastOrDefault()?.Cause ?? BoardingDamageCause.Unspecified;
            var context = ContextFromSimulation(simulation);
            var proposed = _rules.Integrity(new(context, cause, amount, (float)_read("integrity", simulation)!));
            if (Session == context.SessionId) result = proposed;
        });
        return result;
    }
    private static float Multiply(float value, float multiplier)
    {
        var result = (double)value * multiplier;
        if (double.IsNaN(result) || double.IsInfinity(result) || result < 0 || result > float.MaxValue) throw new ArgumentOutOfRangeException(nameof(multiplier));
        return (float)result;
    }
    public void Dispose() { _stopped = true; _scopes.Clear(); _tuned.Clear(); _rules.Dispose(); }
    internal sealed class DisablePlan
    {
        internal readonly object Native, Damage;
        internal readonly BoardingDisableContext Context;
        internal readonly BoardingDisableDecision Decision;
        internal DisablePlan(object native, object damage, BoardingDisableContext context, BoardingDisableDecision decision)
        { Native = native; Damage = damage; Context = context; Decision = decision; }
    }
    private sealed class Scope
    {
        internal readonly BoardingEncounterContext Context;
        internal readonly object? Native;
        internal readonly BoardingDamageCause? Cause;
        internal readonly bool Estimate;
        internal Scope(BoardingEncounterContext context, object? native, BoardingDamageCause? cause, bool estimate)
        { Context = context; Native = native; Cause = cause; Estimate = estimate; }
    }
}

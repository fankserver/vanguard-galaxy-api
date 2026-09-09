using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonRewardService : IDungeonRewardService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IServiceStatus _status;
    private readonly Action<string, Exception> _report;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private bool _disposed, _evaluating;
    internal DungeonRewardService(LifecycleHub hub, Action<string, Exception> report) { _hub = hub; _report = report; _status = hub.Services.Get("dungeon-rewards"); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public bool IsEvaluating { get { _hub.CheckThread(); return _evaluating; } }
    public IDungeonRewardProvider AcquireProvider(string pluginId)
    {
        _hub.CheckThread(); if (_disposed) throw new ObjectDisposedException(nameof(DungeonRewardService));
        _ = new DungeonDefinitionId(pluginId, "rewards");
        if (_providers.ContainsKey(pluginId)) throw new InvalidOperationException("Reward provider already acquired.");
        var provider = new Provider(this, pluginId); _providers.Add(pluginId, provider); return provider;
    }
    private bool Current(DungeonRewardContext context) => !_disposed && Availability.IsAvailable && _hub.CurrentSession?.Id == context.Operation.SessionId &&
        _hub.CurrentSession.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;
    internal double Apply(DungeonRewardContext context)
    {
        _hub.CheckThread();
        if (!Current(context) || _evaluating || _hub.IsDispatchingCallbacks || context.MissionTokenCapture) return context.NativeAmount;
        var entries = _providers.Values.OrderBy(p => p.Id, StringComparer.Ordinal).SelectMany(p => p.Entries.Values.OrderBy(e => e.Id, StringComparer.Ordinal))
            .Where(e => e.Kind == context.Kind).ToArray();
        var multiplier = 1d; _evaluating = true;
        try
        {
            foreach (var entry in entries)
            {
                if (!Current(context)) return context.NativeAmount;
                if (!entry.Active) continue;
                try
                {
                    var adjustment = entry.Policy(context) ?? throw new InvalidOperationException("Null reward adjustment.");
                    if (!entry.Active) continue;
                    var next = multiplier * adjustment.Multiplier;
                    if (next > 100 || double.IsInfinity(context.NativeAmount * next)) throw new ArgumentOutOfRangeException(nameof(adjustment));
                    multiplier = next;
                }
                catch (Exception error) { try { _report(entry.Provider.Id + "/" + entry.Id, error); } catch { } }
            }
            return Current(context) ? context.NativeAmount * multiplier : context.NativeAmount;
        }
        finally { _evaluating = false; }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("dungeon-rewards", false, "Reward service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var provider in _providers.Values.ToArray()) provider.Dispose();
    }
    private sealed class Provider : IDungeonRewardProvider
    {
        internal readonly DungeonRewardService Owner;
        internal readonly string Id;
        internal readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        private bool _disposed;
        internal Provider(DungeonRewardService owner, string id) { Owner = owner; Id = id; }
        public IDisposable Register(string localId, DungeonRewardKind kind, Func<DungeonRewardContext, DungeonRewardAdjustment> policy)
        {
            Owner._hub.CheckThread(); if (_disposed || Owner._disposed) throw new ObjectDisposedException(nameof(Provider));
            _ = new DungeonDefinitionId(Id, localId);
            if (!Enum.IsDefined(typeof(DungeonRewardKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            if (Entries.ContainsKey(localId)) throw new InvalidOperationException("Duplicate reward policy ID.");
            var entry = new Entry(this, localId, kind, policy ?? throw new ArgumentNullException(nameof(policy))); Entries.Add(localId, entry); return entry;
        }
        public void Dispose()
        {
            Owner._hub.CheckThread(); if (_disposed) return; _disposed = true;
            foreach (var entry in Entries.Values.ToArray()) entry.Dispose(); Owner._providers.Remove(Id);
        }
    }
    private sealed class Entry : IDisposable
    {
        internal readonly Provider Provider;
        internal readonly string Id;
        internal readonly DungeonRewardKind Kind;
        internal readonly Func<DungeonRewardContext, DungeonRewardAdjustment> Policy;
        internal bool Active = true;
        internal Entry(Provider provider, string id, DungeonRewardKind kind, Func<DungeonRewardContext, DungeonRewardAdjustment> policy)
        { Provider = provider; Id = id; Kind = kind; Policy = policy; }
        public void Dispose()
        {
            Provider.Owner._hub.CheckThread(); if (!Active) return; Active = false; Provider.Entries.Remove(Id);
        }
    }
}

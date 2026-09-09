using System;
using System.Collections.Generic;
using System.Threading;

namespace VGModAPI.Core;

internal interface ICraftingCommandBackend
{
    CraftingCommandResult Execute(CraftingCommandRequest request);
    CraftingSettingsSnapshot ReadSettings(Guid sessionId, RecipeStationHandle? station);
}

internal sealed class CraftingCommandService : ICraftingCommandService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly ICraftingJobService _jobs;
    private readonly ICraftingCommandBackend? _backend;
    private readonly IServiceStatus _status;
    private readonly Action<Exception> _report;
    private readonly IDisposable _lifetime;
    private readonly Dictionary<(string Plugin, Guid Request), Entry> _requests = new();
    private bool _busy, _disposed, _faultReported;
    private Exception? _fault;
    internal void RecordFault(Exception error) => Interlocked.CompareExchange(ref _fault, error, null);
    internal Exception? PumpFault()
    {
        _hub.CheckThread();
        if (_fault == null || _disposed || _faultReported) return null;
        _faultReported = true; return _fault;
    }
    private int _serializationDepth, _saveDepth;
    internal CraftingCommandService(LifecycleHub hub, ICraftingJobService jobs, ICraftingCommandBackend? backend, Action<Exception> report)
    {
        _hub = hub; _jobs = jobs; _backend = backend; _report = report;
        _status = hub.Services.Get("crafting-commands");
        hub.Services.WatchFault("crafting-commands", () => Volatile.Read(ref _fault) != null);
        if (backend == null && Availability.IsAvailable) hub.SetCapability("crafting-commands", false, "Command bindings unavailable.");
        _lifetime = hub.Subscribe("vgmodapi.crafting-commands", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _requests.Clear(); _saveDepth = 0; }
            else if (message.Kind == LifecycleEventKind.SaveStarted) _saveDepth++;
            else if (message.Kind is LifecycleEventKind.SaveSucceeded or LifecycleEventKind.SaveFailed or LifecycleEventKind.SaveSkipped)
            { if (_saveDepth > 0) _saveDepth--; }
        });
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    internal void SetAvailable(bool value)
    {
        _hub.CheckThread(); if (_disposed) return;
        _hub.SetCapability("crafting-commands", value && _backend != null,
            value ? "Guarded commands bound." : "Crafting commands unavailable.");
    }
    internal void BeginSerialization() { _hub.CheckThread(); _serializationDepth++; }
    internal void EndSerialization() { _hub.CheckThread(); if (_serializationDepth > 0) _serializationDepth--; }
    public CraftingCommandResult Execute(CraftingCommandRequest request)
    {
        _hub.CheckThread(); if (request == null) throw new ArgumentNullException(nameof(request));
        if (_disposed || _backend == null || !Availability.IsAvailable) return Result(request, CraftingCommandStatus.IntegrationUnavailable, "Crafting commands unavailable.");
        var session = _hub.CurrentSession;
        if (session == null || session.Phase != SessionPhase.GameplayInitialized) return Result(request, CraftingCommandStatus.SessionUnavailable, "Gameplay session required.");
        if (request.SessionId != session.Id) return Result(request, CraftingCommandStatus.StaleHandle, "Request belongs to another session.");
        var key = (request.PluginId, request.RequestId);
        if (_requests.TryGetValue(key, out var existing))
        {
            if (!SameIntent(existing.Request, request)) return Result(request, CraftingCommandStatus.RequestConflict, "Request ID already identifies a different intent.");
            var prior = existing.Result;
            return prior == null ? Result(request, CraftingCommandStatus.Busy, "This request is already executing.") :
                new CraftingCommandResult(prior.RequestId, prior.Status, prior.Detail, prior.MutationMayHaveRun, true, prior.CreditDelta, prior.Jobs, prior.Deliveries);
        }
        if (_busy || _serializationDepth > 0 || _saveDepth > 0 || _hub.IsDispatchingCallbacks || _jobs.IsDispatchingCallbacks)
            return Result(request, CraftingCommandStatus.Busy, "Save, callback or reentrant context refuses mutation.");
        if (_requests.Count >= 4096) return Result(request, CraftingCommandStatus.RequestLimitExceeded, "Session request history is full; uncertain outcomes are never evicted to allow retries.");
        var entry = new Entry(request); _requests.Add(key, entry); _busy = true;
        try
        {
            var outcome = _backend.Execute(request);
            if (_disposed || !Availability.IsAvailable || _hub.CurrentSession?.Id != request.SessionId || outcome.RequestId != request.RequestId)
                outcome = Result(request, CraftingCommandStatus.Uncertain, "Session or result attribution changed during execution; do not retry blindly.", true);
            entry.Result = outcome; return outcome;
        }
        catch (Exception error)
        {
            try { _report(error); } catch { }
            entry.Result = Result(request, CraftingCommandStatus.Uncertain, "Execution faulted; partial effects are possible. Do not retry blindly.", true);
            return entry.Result;
        }
        finally { _busy = false; }
    }
    public CraftingSettingsSnapshot ReadSettings(Guid sessionId, RecipeStationHandle? station = null)
    {
        _hub.CheckThread();
        if (sessionId == Guid.Empty) throw new ArgumentException("Session identity required.", nameof(sessionId));
        if (_disposed || _backend == null || !Availability.IsAvailable || _hub.CurrentSession?.Id != sessionId || _hub.CurrentSession.Phase != SessionPhase.GameplayInitialized ||
            station != null && station.SessionId != sessionId) return new(sessionId, false, null, null, null, null, "Settings context unavailable.");
        try
        {
            var result = _backend.ReadSettings(sessionId, station);
            return !_disposed && Availability.IsAvailable && _hub.CurrentSession?.Id == sessionId && result.SessionId == sessionId ? result :
                new(sessionId, false, null, null, null, null, "Settings context changed during read.");
        }
        catch (Exception error) { try { _report(error); } catch { } return new(sessionId, false, null, null, null, null, "Settings read failed."); }
    }
    private static bool SameIntent(CraftingCommandRequest a, CraftingCommandRequest b) => a.SessionId == b.SessionId && a.Kind == b.Kind &&
        Equals(a.Station, b.Station) && Equals(a.Recipe, b.Recipe) && Equals(a.Job, b.Job) && Equals(a.Material, b.Material) &&
        a.Count == b.Count && a.Protection == b.Protection && a.Setting == b.Setting && a.SettingValue == b.SettingValue;
    internal static CraftingCommandResult Result(CraftingCommandRequest request, CraftingCommandStatus status, string detail, bool invoked = false) =>
        new(request.RequestId, status, detail, invoked);
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _lifetime.Dispose(); _requests.Clear();
        if (Availability.IsAvailable) _hub.SetCapability("crafting-commands", false, "Command service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private sealed class Entry
    {
        internal readonly CraftingCommandRequest Request;
        internal CraftingCommandResult? Result;
        internal Entry(CraftingCommandRequest request) { Request = request; }
    }
}

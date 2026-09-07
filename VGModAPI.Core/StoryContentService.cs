using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

/// <summary>
/// The API-owned story module: authenticated provider leases in, automatic persistence out. It
/// registers its OWN persistence provider, so a consumer supplies no codec, no save/load callback
/// and no restoration scheduling for supported story state.
///
/// LIFETIME: this module is API-root scoped and must be constructed before any session starts and
/// disposed only at API shutdown. Its persistence owner is unregistered only by that shutdown,
/// because unregistering an owner mid-session pauses coordinated saves for EVERY registered mod. A
/// consumer's lease disposal therefore releases only that provider's registrations.
///
/// AVAILABILITY: a query answers for the CURRENT session only. The module resets its ledger on
/// session start and on any invalidated or failed start, and reports Known only for a session whose
/// state it actually restored (including a fresh new game with no stored generation). Schema-
/// unsupported, corrupt, restore-failed and load-blocked owners never call restore, so those
/// sessions stay Unavailable instead of answering from the previous save.
///
/// This type is pure with respect to the game: it decides what must be installed and what must be
/// remembered. Driving vanilla registration/reconstruction is separate work.
/// </summary>
internal sealed class StoryContentService : IStoryApi, IDisposable
{
    private enum Readiness { None, Pending, Restored, Blocked }

    private sealed class Lease : IStoryProvider
    {
        private readonly StoryContentService _service;
        private bool _disposed;
        internal StoryHostPlugin Plugin { get; }
        public string ProviderId { get; }
        internal Lease(StoryContentService service, StoryHostPlugin plugin, string providerId)
        { _service = service; Plugin = plugin; ProviderId = providerId; }

        public bool Active { get { _service.CheckThread(); return !_disposed && !_service._disposed; } }

        public StoryRegistrationResult Register(StoryMissionDefinition definition)
        {
            _service.CheckThread();
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (!Active) return new StoryRegistrationResult(StoryRegistrationStatus.Unavailable, null, "This provider lease is no longer active.");
            return _service.Register(this, definition);
        }

        public StoryTransitionResult Offer(Guid expectedSessionId, string localId)
        {
            _service.CheckThread();
            return _service.Offer(this, expectedSessionId, localId);
        }

        public StoryTransitionResult Activate(Guid expectedSessionId, Guid occurrenceId)
        {
            _service.CheckThread();
            return _service.Activate(this, expectedSessionId, occurrenceId);
        }

        public StoryTransitionResult Withdraw(Guid expectedSessionId, Guid occurrenceId)
        {
            _service.CheckThread();
            var result = _service.Transition(this, expectedSessionId, occurrenceId,
                (StoryLedger ledger, StoryContentId id, out string diagnostic) => ledger.Withdraw(id, occurrenceId, out diagnostic));
            if (result.Accepted) { _service._declaredChoices.Remove(occurrenceId); _service.ReleaseOccurrenceEntry(occurrenceId); }
            return result;
        }

        public StoryTransitionResult DeclareChoices(Guid expectedSessionId, Guid occurrenceId, IReadOnlyDictionary<string, string> choices)
        {
            _service.CheckThread();
            if (choices == null) throw new ArgumentNullException(nameof(choices));
            return _service.DeclareChoices(this, expectedSessionId, occurrenceId, choices);
        }

        public StoryTransitionResult Retire(Guid expectedSessionId, Guid occurrenceId, StoryOutcome outcome, IReadOnlyDictionary<string, string>? choices = null)
        {
            _service.CheckThread();
            return _service.Retire(this, expectedSessionId, occurrenceId, outcome, choices);
        }

        public StoryOccurrenceQuery Occurrences(string localId)
        {
            _service.CheckThread();
            return _service.Occurrences(this, localId);
        }

        public StoryOccurrenceSnapshotQuery Unresolved(string localId)
        {
            _service.CheckThread();
            return _service.Unresolved(this, localId);
        }

        public StoryCompletionQuery IsCompleted(string localId)
        {
            _service.CheckThread();
            return _service.IsCompleted(this, localId);
        }

        public void Dispose()
        {
            _service.CheckThread();
            if (_disposed) return;
            _disposed = true;
            // Releases this provider's registrations only. The module's persistence owner stays
            // registered, so no other mod's saves are paused by a consumer's teardown.
            _service.ReleaseProvider(this);
        }
    }

    private delegate StoryLedgerStatus LedgerCall(StoryLedger ledger, StoryContentId id, out string diagnostic);

    private readonly StoryDefinitionRegistry _registry = new();
    private readonly StoryLedger _ledger = new();
    private readonly Dictionary<string, Lease> _leasesBySegment = new(StringComparer.Ordinal);
    private readonly StoryProviderBindings _bindings = new();
    private readonly Func<Guid> _newOccurrence;
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action? _checkThread;
    private readonly Func<SessionSnapshot?> _currentSession;
    /// <summary>
    /// The module's own registration handle. Readiness is an OPTIONAL capability
    /// (<see cref="IPersistenceReadiness"/>) on that handle: the runtime implementation provides it,
    /// and a handle that does not is treated as "readiness unknown", which makes every answer
    /// unavailable rather than assuming restored state exists.
    /// </summary>
    private readonly IPersistenceRegistration? _persistence;
    /// <summary>
    /// The native world this module installs into and drives. Without it the module owns definitions
    /// and state but nothing exists in the game, so accepting content is refused rather than recorded:
    /// a caller must never be able to claim an acceptance the world never made.
    /// </summary>
    private readonly IStoryWorld? _world;
    private readonly List<string> _reconciliation = new();
    /// <summary>The catalog identifier each occurrence is installed under, so ownership survives a lease.</summary>
    private readonly Dictionary<Guid, string> _occurrenceIdentifiers = new();
    /// <summary>Occurrence entries whose removal is waiting for an operation that is still running.</summary>
    private readonly List<string> _deferredUninstall = new();
    /// <summary>Outcomes this module itself is applying, so its own world calls are not re-observed as the game's.</summary>
    private readonly Dictionary<Guid, StoryOutcome> _intent = new();
    /// <summary>Choices staged for an occurrence whose outcome the game will produce.</summary>
    private readonly Dictionary<Guid, Dictionary<string, string>> _declaredChoices = new();
    private readonly IDisposable? _missionObserver;
    private readonly Action<string>? _report;
    private bool _operationInFlight;
    private string? _suspended;
    private string? _fault;
    private readonly IDisposable? _lifecycle;
    private Readiness _readiness = Readiness.None;
    private Guid _restoredSession;
    private string _readinessDetail = "no session has started since this module was created";
    private bool _disposed;

    /// <summary>
    /// Constructs the module. It MUST be constructed before any session begins, because the
    /// persistence coordinator refuses a new owner once a session is live; that precondition is
    /// checked here so the module never leaves a half-registered owner, a dangling subscription or a
    /// paused coordinator behind.
    /// </summary>
    /// <exception cref="InvalidOperationException">A session is already running.</exception>
    internal StoryContentService(IPersistenceApi? persistence, ILifecycleApi? lifecycle, StoryHostAuthenticator authenticate,
        Func<Guid>? newOccurrence = null, Action? checkThread = null, IStoryWorld? world = null,
        IMissionEvents? missions = null, Action<string>? report = null)
    {
        checkThread?.Invoke();
        if (lifecycle?.CurrentSession != null)
            throw new InvalidOperationException("The story module must be constructed before a session begins.");
        _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        _world = world;
        _report = report;
        _newOccurrence = newOccurrence ?? Guid.NewGuid;
        _checkThread = checkThread;
        _currentSession = () => lifecycle?.CurrentSession;
        // The module owns its persistence: capture and restore are ITS callbacks, not a consumer's.
        _persistence = persistence?.Register(new PersistenceProvider(
            StoryStateCodec.Owner, StoryStateCodec.SchemaVersion,
            capture: () => StoryStateCodec.Encode(_ledger.Entries),
            restore: OnRestore,
            validate: StoryStateCodec.Validate));
        // Availability is bound to the lifecycle independently of restore: a failed or invalidated
        // session never calls restore, and its queries must not answer from the previous save.
        _lifecycle = lifecycle?.Subscribe("vgmodapi.story-content", OnLifecycle);
        // Outcomes are OBSERVED, not declared: the game completing or failing an owned mission is what
        // records a completion, so this module watches the same mission boundary every consumer sees.
        _missionObserver = missions?.Subscribe("vgmodapi.story-content", OnMissionTransition);
    }

    private void CheckThread() => _checkThread?.Invoke();

    private void OnRestore(SessionSnapshot session, byte[]? bytes)
    {
        _ledger.Restore(bytes == null ? Array.Empty<StoryOccurrenceEntry>() : StoryStateCodec.Decode(bytes));
        _restoredSession = session?.Id ?? _currentSession()?.Id ?? Guid.Empty;
        if (_restoredSession == Guid.Empty)
        {
            _readiness = Readiness.Blocked;
            _readinessDetail = "restored state could not be associated with a session";
            return;
        }
        _readiness = Readiness.Restored;
        _readinessDetail = bytes == null ? "no stored story state for this save" : "restored";
        Reconcile();
    }

    /// <summary>
    /// Correlates the restored ledger with what the world actually holds, by story identifier. Vanilla
    /// persists ACCEPTED missions itself, so the two can legitimately disagree: the world may have
    /// ended a mission while this module was not the one observing it, and a save may carry one of our
    /// identifiers that this ledger never admitted. Neither is repaired by inventing state. Nothing is
    /// deleted, no acceptance is fabricated, and the disagreement is recorded so a refusal can explain
    /// itself instead of pretending the two agree.
    /// </summary>
    private void Reconcile()
    {
        _reconciliation.Clear();
        _suspended = null;
        if (_world == null) return;
        // Unresolved occurrences need their own catalog entry back before anything can be accepted or
        // observed for them. A definition that is no longer registered cannot be reinstalled, which is
        // exactly the provider-absence case handled below.
        foreach (var entry in _ledger.Entries)
        {
            if (entry.State == StoryOccurrenceState.Retired) continue;
            var identifier = StoryContentPolicy.OccurrenceIdentifier(entry.Id, entry.OccurrenceId);
            if (!_registry.TryGet(entry.Id, out var definition))
            { Suspend(identifier + ": this save holds owned story content whose provider is not registered."); continue; }
            var installed = _world.Install(identifier, definition);
            if (installed.Applied) _occurrenceIdentifiers[entry.OccurrenceId] = identifier;
            else Suspend(identifier + ": the world refused to reinstall this occurrence (" + installed.Detail + ").");
        }
        var snapshot = _world.Snapshot();
        if (snapshot == null) return;
        var active = new HashSet<string>(snapshot.Active, StringComparer.Ordinal);
        var archived = new HashSet<string>(snapshot.Archived, StringComparer.Ordinal);
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _ledger.Entries)
        {
            if (entry.State != StoryOccurrenceState.Active) continue;
            var identifier = StoryContentPolicy.OccurrenceIdentifier(entry.Id, entry.OccurrenceId);
            admitted.Add(identifier);
            if (active.Contains(identifier)) continue;
            _reconciliation.Add(identifier + ": recorded active, but the world holds no such mission"
                + (archived.Contains(identifier) ? " (it is archived there)" : "") + ".");
        }
        foreach (var identifier in active)
        {
            if (!StoryContentPolicy.TryParseOccurrenceIdentifier(identifier, out var id, out var occurrence)) continue;
            if (admitted.Contains(identifier)) continue;
            // One of OUR identifiers is live in the world without an active occurrence here: an
            // orphan. It is NOT adopted, because occurrence identity is minted by this module and
            // never inferred from a save, and it is NOT removed, because the save is the player's.
            // The module suspends itself instead, so nothing else is accepted or recorded on top of a
            // world it does not understand, and the saved bytes are left exactly as they are.
            _reconciliation.Add(identifier + ": the world holds this mission, but no admitted occurrence claims it.");
            Suspend(identifier + ": this save holds owned story content this module cannot account for"
                + (_registry.Contains(id) ? "" : "; its provider is not registered") + ".");
            _ = occurrence;
        }
    }

    /// <summary>
    /// Stops this module for the rest of the session, fail-closed. Nothing native is removed and no
    /// persisted byte is rewritten: the content stays exactly as the save holds it, and the reason is
    /// reported so the host can surface a provider-required compatibility state.
    /// </summary>
    private void Suspend(string reason)
    {
        if (_suspended == null) { _suspended = reason; Report("Owned story content is suspended for this session: " + reason); }
        if (!_reconciliation.Contains(reason)) _reconciliation.Add(reason);
    }

    private void Report(string detail) { try { _report?.Invoke(detail); } catch { /* reporting must never fault the module */ } }

    /// <summary>Blocks the module after a native operation could not be undone, so nothing builds on an unknown world.</summary>
    private void Fault(string reason)
    {
        _fault = reason;
        Report("Owned story content is blocked: " + reason);
    }

    internal string? SuspendedReason => _suspended;
    internal string? FaultReason => _fault;

    /// <summary>Disagreements found when the restored ledger was correlated with the world, if any.</summary>
    internal IReadOnlyList<string> Reconciliation => _reconciliation.ToArray();

    /// <summary>
    /// Records the outcome the GAME produced for an owned occurrence. A completion is only ever
    /// written from here, so no caller can claim one for content that was never accepted or that the
    /// player abandoned. A neutral removal says nothing about why the mission ended: unless this
    /// module caused it, the occurrence stays unresolved rather than being called complete or failed.
    /// </summary>
    private void OnMissionTransition(MissionTransition transition)
    {
        if (_disposed || transition?.Mission?.DefinitionId == null) return;
        if (!StoryContentPolicy.TryParseOccurrenceIdentifier(transition.Mission.DefinitionId, out var id, out var occurrenceId)) return;
        if (!_ledger.TryGet(occurrenceId, out var entry) || entry.Id != id || entry.State == StoryOccurrenceState.Retired) return;
        // A removal this module is performing is recorded by the operation that asked for it.
        if (_intent.ContainsKey(occurrenceId)) return;
        StoryOutcome? outcome = transition.Kind switch
        {
            MissionTransitionKind.Completed => StoryOutcome.Completed,
            MissionTransitionKind.Failed => StoryOutcome.Failed,
            MissionTransitionKind.Abandoned => StoryOutcome.Abandoned,
            _ => null
        };
        if (outcome == null) return;
        _declaredChoices.TryGetValue(occurrenceId, out var declared);
        var status = _ledger.Retire(entry.Id, occurrenceId, outcome.Value, declared, out var diagnostic);
        if (status != StoryLedgerStatus.Accepted)
        { Report("An observed " + outcome + " for '" + transition.Mission.DefinitionId + "' could not be recorded: " + diagnostic); return; }
        _declaredChoices.Remove(occurrenceId);
        ReleaseOccurrenceEntry(occurrenceId);
    }

    private void OnLifecycle(LifecycleEvent e)
    {
        if (_disposed || e == null) return;
        switch (e.Kind)
        {
            case LifecycleEventKind.SessionStarting:
                // A new attempt owns no earlier state, restored or not. Reservations describe the
                // WORLD, not the process, so they are dropped with it and re-evaluated against the
                // actual native world of the new session.
                _ledger.Reset();
                _registry.ResetWorldReservations();
                _restoredSession = Guid.Empty;
                _readiness = Readiness.Pending;
                _readinessDetail = "the session has not restored story state yet";
                break;
            case LifecycleEventKind.SessionInvalidated:
            case LifecycleEventKind.SessionStartFailed:
                _ledger.Reset();
                _registry.ResetWorldReservations();
                _restoredSession = Guid.Empty;
                _readiness = Readiness.Blocked;
                _readinessDetail = e.Kind == LifecycleEventKind.SessionStartFailed ? "the session failed to start" : "the session was invalidated";
                break;
        }
    }

    /// <summary>
    /// Null when the module can ANSWER for the current session, otherwise the exact reason it cannot.
    ///
    /// This is owner READINESS, not permission to mutate. Reading restored state is safe while
    /// lifecycle callbacks dispatch and while a save is in flight — a provider rediscovering its
    /// content from a GameplayInitialized callback is the documented way to use this API — whereas an
    /// owner whose data was blocked, unreadable or restore-failed has no state to report at all. The
    /// owner's readiness is re-read on EVERY answer, so a mid-session block still stops the module
    /// from reporting state that will not reach disk as this save's known history.
    /// </summary>
    private string? Unavailable()
    {
        if (_disposed) return "the story module is disposed";
        if (_persistence is not IPersistenceReadiness readiness) return "story persistence does not report readiness";
        if (!readiness.StateReady) return "story persistence is " + PersistenceStatus;
        if (_readiness != Readiness.Restored) return _readinessDetail;
        var session = _currentSession();
        if (session == null || session.Id != _restoredSession) return "the restored session is no longer current";
        if (session.Phase is SessionPhase.Failed or SessionPhase.Invalidated) return "the current session is " + session.Phase;
        return null;
    }

    internal string PersistenceStatus => _disposed ? "inactive" : _persistence?.Status ?? "unavailable";
    internal StoryLedger Ledger => _ledger;
    internal StoryDefinitionRegistry Registry => _registry;
    internal string ReadinessDetail => _readinessDetail;

    /// <summary>Identifiers that already exist in the world; reserving them keeps registration fail-closed.</summary>
    internal void ReserveExistingIdentifiers(IEnumerable<string> identifiers) => _registry.Reserve(identifiers);

    /// <summary>
    /// Public entry point. It is deliberately NOT inlined, so <see cref="Assembly.GetCallingAssembly"/>
    /// observes the consumer's own frame: interface dispatch adds no frame, so the immediate caller
    /// of this body is the code that made the API call. The captured assembly is passed to the host
    /// authenticator and is never a parameter, so no argument can claim to come from elsewhere.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public StoryProviderResult AcquireProvider(object pluginInstance)
        => AcquireProviderFor(pluginInstance, Assembly.GetCallingAssembly());

    private StoryProviderResult AcquireProviderFor(object pluginInstance, Assembly callingAssembly)
    {
        CheckThread();
        if (pluginInstance == null) throw new ArgumentNullException(nameof(pluginInstance));
        if (_disposed) return new StoryProviderResult(StoryProviderStatus.Unavailable, null, "The story module is disposed.");
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, callingAssembly); }
        catch { plugin = null; }
        if (plugin == null)
            return new StoryProviderResult(StoryProviderStatus.UnknownPlugin, null,
                "The host could not resolve this object to a loaded plugin, so no provider identity can be derived.");
        // The host resolved the ARGUMENT; this checks it against the assembly that actually called.
        // An assembly declaring several plugins can still acquire any of its own, which is why this
        // is an ordinary-use boundary rather than a sandbox.
        if (callingAssembly == null || !ReferenceEquals(plugin.Assembly, callingAssembly))
            return new StoryProviderResult(StoryProviderStatus.CallerMismatch, null,
                "That plugin instance belongs to a plugin loaded from another assembly than the caller.");
        var segment = StoryProviderIdentity.Segment(plugin);
        var binding = _bindings.Bind(segment, plugin.PluginId);
        if (binding == StoryBindingStatus.Conflict)
            return new StoryProviderResult(StoryProviderStatus.ProviderConflict, null,
                "Provider segment '" + segment + "' is already bound to another host plugin.");
        if (binding == StoryBindingStatus.LimitExceeded)
            return new StoryProviderResult(StoryProviderStatus.LimitExceeded, null,
                "All " + StoryProviderBindings.MaxProviders + " story provider slots are bound; an existing provider's reserved share is never taken away.");
        // A lease is not shared: a second acquisition is refused so one holder's Dispose can never
        // revoke another holder's registrations behind its back.
        if (_leasesBySegment.TryGetValue(segment, out var existing))
        {
            if (existing.Active)
                return new StoryProviderResult(StoryProviderStatus.AlreadyAcquired, null,
                    "This plugin already holds a live story provider lease; cache and reuse it.");
            _leasesBySegment.Remove(segment);
        }
        var lease = new Lease(this, plugin, segment);
        _leasesBySegment[segment] = lease;
        return new StoryProviderResult(StoryProviderStatus.Acquired, lease, "");
    }

    private StoryRegistrationResult Register(Lease lease, StoryMissionDefinition definition)
    {
        var id = new StoryContentId(lease.ProviderId, definition.LocalId);
        var status = _registry.TryRegister(id, definition, out var diagnostic, out var identifier, out var entry);
        if (status != StoryRegistrationStatus.Registered) return new StoryRegistrationResult(status, null, diagnostic);
        if (_world != null)
        {
            // The base definition is installed so the identifier is visibly ours and a collision with
            // existing content is refused rather than overwritten. Each OCCURRENCE later gets its own
            // catalog entry, because the game archives a completed story identifier permanently.
            var installed = _world.Install(identifier, definition);
            if (!installed.Applied)
            {
                _registry.RemoveIfMatches(id, entry);
                return installed.Status == StoryWorldStatus.AlreadyPresent
                    ? new StoryRegistrationResult(StoryRegistrationStatus.IdentifierInUse, null,
                        "Identifier '" + identifier + "' already exists in this world; the API never replaces existing content.")
                    : new StoryRegistrationResult(StoryRegistrationStatus.Unavailable, null,
                        "The story world refused this definition: " + installed.Detail);
            }
            // The source faction is resolved against the GAME's own registry: the game writes
            // sourceFaction.identifier unconditionally when it saves a held mission, so a definition
            // naming a faction the game does not know would produce a mission that breaks the save.
            if (!_world.KnowsFaction(definition.SourceFaction.Value))
            {
                _world.Uninstall(identifier);
                _registry.RemoveIfMatches(id, entry);
                return new StoryRegistrationResult(StoryRegistrationStatus.InvalidDefinition, null,
                    "The game does not know source faction '" + definition.SourceFaction + "'.");
            }
        }
        return new StoryRegistrationResult(status, new Registration(this, lease, id, identifier, entry), "");
    }

    private sealed class Registration : IStoryRegistration
    {
        private readonly StoryContentService _service;
        private readonly Lease _lease;
        private readonly long _entry;
        private bool _disposed;
        public StoryContentId Id { get; }
        public string NativeIdentifier { get; }
        internal Registration(StoryContentService service, Lease lease, StoryContentId id, string identifier, long entry)
        { _service = service; _lease = lease; Id = id; NativeIdentifier = identifier; _entry = entry; }
        public bool Active
        {
            get
            {
                _service.CheckThread();
                return !_disposed && _lease.Active && _service._registry.EntryOf(Id) == _entry;
            }
        }
        public void Dispose()
        {
            _service.CheckThread();
            if (_disposed) return;
            _disposed = true;
            // A handle from a released lease is stale: the identifier may already belong to a NEW
            // registration made through a re-acquired lease, and this handle must never remove it.
            if (!_lease.Active) return;
            // Even under the SAME live lease the identifier may have been registered again since this
            // handle was issued, with the same immutable definition object; only the registration this
            // handle actually made is released. Saved occurrences are never rewritten or deleted here.
            if (_service._registry.RemoveIfMatches(Id, _entry)) _service._world?.Uninstall(NativeIdentifier);
        }
    }

    private StoryTransitionResult Offer(Lease lease, Guid expectedSessionId, string localId)
    {
        if (!TryDefinition(lease, expectedSessionId, localId, out var id, out var definition, out var refusal, out var status))
            return new StoryTransitionResult(status, Guid.Empty, refusal);
        var occurrenceId = _newOccurrence();
        // The outcome's worst-case payload is reserved now, so this occurrence can always be retired.
        var result = _ledger.Offer(id, definition!.Retention, occurrenceId, definition.ReservedChoiceBytes, out var diagnostic);
        if (result != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(result), Guid.Empty, diagnostic);
        if (_world != null)
        {
            // Every occurrence gets its OWN catalog entry. The game archives a completed story
            // identifier and refuses a duplicate of it forever, so a shared identifier could be
            // accepted exactly once per save; a repeated occurrence needs an identifier of its own.
            var identifier = StoryContentPolicy.OccurrenceIdentifier(id, occurrenceId);
            var installed = _world.Install(identifier, definition);
            if (!installed.Applied)
            {
                _ledger.Withdraw(id, occurrenceId, out _);
                return new StoryTransitionResult(
                    installed.Status == StoryWorldStatus.Unavailable ? StoryTransitionStatus.Unavailable : StoryTransitionStatus.InvalidTransition,
                    Guid.Empty, "The world refused this occurrence: " + installed.Detail);
            }
            _occurrenceIdentifiers[occurrenceId] = identifier;
        }
        return new StoryTransitionResult(StoryTransitionStatus.Accepted, occurrenceId, diagnostic);
    }

    /// <summary>
    /// Removes the catalog entry of an occurrence that no longer needs one. It is deferred while a
    /// native operation is in flight, because the entry is what that operation is acting on.
    /// </summary>
    private void ReleaseOccurrenceEntry(Guid occurrenceId)
    {
        if (!_occurrenceIdentifiers.TryGetValue(occurrenceId, out var identifier)) return;
        _occurrenceIdentifiers.Remove(occurrenceId);
        if (_operationInFlight) { _deferredUninstall.Add(identifier); return; }
        _world?.Uninstall(identifier);
    }

    /// <summary>
    /// Marks a native operation as running. Accepting or abandoning a mission dispatches the game's
    /// own mission observers synchronously, so consumer code runs INSIDE this call: another story
    /// mutation from there is refused as busy rather than interleaved, and any catalog removal a
    /// disposal asks for waits until the operation this module is in the middle of has finished.
    /// </summary>
    private bool BeginOperation(out StoryTransitionResult refusal, Guid occurrenceId)
    {
        refusal = default!;
        if (_operationInFlight)
        {
            refusal = new StoryTransitionResult(StoryTransitionStatus.Busy, occurrenceId,
                "Another owned-story operation is already running on this thread; retry after it completes.");
            return false;
        }
        _operationInFlight = true;
        return true;
    }

    private void EndOperation()
    {
        _operationInFlight = false;
        foreach (var identifier in _deferredUninstall) _world?.Uninstall(identifier);
        _deferredUninstall.Clear();
    }

    /// <summary>
    /// Records the outcome. Declared choices must be the ones the definition DECLARED: the space for
    /// them was reserved when the occurrence was offered, so an undeclared key is refused here rather
    /// than accepted into space that was never held for it.
    ///
    /// The supplied collection belongs to the caller, so it is COPIED exactly once and everything
    /// afterwards — the declared-key check, the ledger's bounds and the stored record — reads only
    /// that copy. Validating one view of a caller collection and storing another would let stored
    /// choices exceed the bounds that were checked, and every later capture would then fail, which
    /// the coordinator turns into a save block for every registered mod.
    /// </summary>
    /// <summary>
    /// Accepting an offered occurrence is a WORLD operation first and a record second: the API asks
    /// vanilla to accept the mission it installed, verifies that the world actually holds it, and only
    /// then records the activation. A world refusal — no player, a duplicate story identifier, an
    /// unverifiable result — leaves the ledger exactly as it was, so a caller can never hold an
    /// activation the game never made.
    /// </summary>
    private StoryTransitionResult Activate(Lease lease, Guid expectedSessionId, Guid occurrenceId)
    {
        // Everything is validated BEFORE the world is touched, because the acceptance runs the game's
        // mission observers - consumer code - inside it, and a refusal discovered afterwards would
        // otherwise leave the world holding a mission this ledger never recorded.
        if (!Guard(lease, expectedSessionId, out var refusal, out var status))
            return new StoryTransitionResult(status, occurrenceId, refusal);
        var caller = new StoryContentId(lease.ProviderId, LocalIdOf(occurrenceId));
        var planned = _ledger.CanActivate(caller, occurrenceId, out var plannedDetail);
        if (planned != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(planned), occurrenceId, plannedDetail);
        if (_world == null)
            return new StoryTransitionResult(StoryTransitionStatus.Unavailable, occurrenceId,
                "No story world is bound, so this acceptance cannot be made in the game and is not recorded.");
        if (!_occurrenceIdentifiers.TryGetValue(occurrenceId, out var identifier))
            return new StoryTransitionResult(StoryTransitionStatus.Unavailable, occurrenceId,
                "This occurrence has no installed catalog entry in the current world.");
        if (!BeginOperation(out var busy, occurrenceId)) return busy;
        StoryWorldResult accepted;
        try { accepted = _world.Accept(identifier); }
        finally { EndOperation(); }
        if (!accepted.Applied)
            return new StoryTransitionResult(
                accepted.Status == StoryWorldStatus.Unavailable ? StoryTransitionStatus.Unavailable : StoryTransitionStatus.InvalidTransition,
                occurrenceId, "The world did not accept this mission: " + accepted.Detail);
        // Consumer code ran inside that call and may have disposed the lease, reloaded or otherwise
        // invalidated this operation. Re-validate, and if the record can no longer be written, undo
        // the exact acceptance instead of leaving the world holding an unrecorded mission.
        if (!Guard(lease, expectedSessionId, out refusal, out status)
            || _ledger.CanActivate(caller, occurrenceId, out refusal) != StoryLedgerStatus.Accepted)
        {
            var rollback = _world.RollbackAccept(identifier);
            if (!rollback.Applied)
            {
                Fault("an acceptance of '" + identifier + "' could not be undone after the record was refused: " + rollback.Detail);
                return new StoryTransitionResult(StoryTransitionStatus.Unavailable, occurrenceId,
                    "The world accepted this mission but the record was refused and the acceptance could not be undone; owned story content is blocked for this session.");
            }
            return new StoryTransitionResult(status == StoryTransitionStatus.Accepted ? StoryTransitionStatus.InvalidTransition : status,
                occurrenceId, "The acceptance was undone because it could no longer be recorded: " + refusal);
        }
        var committed = _ledger.Activate(caller, occurrenceId, out var diagnostic);
        return new StoryTransitionResult(Map(committed), occurrenceId, diagnostic);
    }

    /// <summary>
    /// What the world must do before a terminal outcome is recorded. A completion belongs to the game:
    /// while the world still holds the mission the API refuses rather than claiming one and granting no
    /// reward. An abandonment or failure the caller declares is applied to the world first, only for a
    /// mission this API installed, and only recorded once the world no longer holds it.
    /// </summary>
    private string? ReleaseInWorld(StoryOccurrenceEntry entry, StoryOutcome outcome, out bool unavailable)
    {
        unavailable = false;
        if (_world == null || entry.State != StoryOccurrenceState.Active) return null;
        if (!_occurrenceIdentifiers.TryGetValue(entry.OccurrenceId, out var identifier))
        { unavailable = true; return "This occurrence has no installed catalog entry in the current world."; }
        var released = _world.Release(identifier, outcome);
        if (released.Applied) return null;
        unavailable = released.Status == StoryWorldStatus.Unavailable;
        return "The world did not end this mission: " + released.Detail;
    }

    /// <summary>
    /// Stages the choices an observed outcome will carry. It validates exactly what a retirement
    /// validates — one snapshot of the caller's collection, declared keys only, the reservation taken
    /// at offer time — so the outcome the game later produces can always be recorded with them.
    /// </summary>
    private StoryTransitionResult DeclareChoices(Lease lease, Guid expectedSessionId, Guid occurrenceId,
        IReadOnlyDictionary<string, string> choices)
    {
        if (!ValidateChoices(lease, expectedSessionId, occurrenceId, choices, out var owned, out var refusal)) return refusal;
        var caller = new StoryContentId(lease.ProviderId, LocalIdOf(occurrenceId));
        var planned = _ledger.CanRetire(caller, occurrenceId, StoryOutcome.Completed, owned, out var plannedDetail);
        if (planned != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(planned), occurrenceId, plannedDetail);
        _declaredChoices[occurrenceId] = owned!;
        return new StoryTransitionResult(StoryTransitionStatus.Accepted, occurrenceId, "");
    }

    /// <summary>The shared validation of a caller's choice collection: snapshot once, then check ownership and keys.</summary>
    private bool ValidateChoices(Lease lease, Guid expectedSessionId, Guid occurrenceId,
        IReadOnlyDictionary<string, string>? choices, out Dictionary<string, string>? owned, out StoryTransitionResult refusal)
    {
        owned = null;
        refusal = default!;
        if (choices == null) return true;
        // Availability, lease and session are checked BEFORE anything is looked up, and ownership
        // is resolved by the ledger's own rules, so this path can never answer differently from
        // the choice-free path or mention another provider's local ID. Reading a foreign caller's
        // collection is itself deferred until after that authorisation.
        if (!Guard(lease, expectedSessionId, out var detail, out var status))
        { refusal = new StoryTransitionResult(status, occurrenceId, detail); return false; }
        var resolved = _ledger.ResolveOwned(new StoryContentId(lease.ProviderId, LocalIdOf(occurrenceId)), occurrenceId,
            out var entry, out var ownership);
        if (resolved != StoryLedgerStatus.Accepted)
        { refusal = new StoryTransitionResult(Map(resolved), occurrenceId, ownership); return false; }
        if (!TrySnapshotChoices(choices, out var copy, out var unreadable))
        { refusal = new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId, unreadable); return false; }
        owned = copy;
        // Reading the caller's collection ran ITS code on this thread, which may have disposed the
        // lease, retired this occurrence or reloaded the save. Authorisation is therefore
        // re-established before anything else is consulted, so a caller that invalidated itself
        // hears that, not a diagnostic about the state it just changed.
        if (!Guard(lease, expectedSessionId, out detail, out status))
        { refusal = new StoryTransitionResult(status, occurrenceId, detail); return false; }
        if (copy.Count > 0)
        {
            var id = new StoryContentId(lease.ProviderId, entry!.Id.LocalId);
            if (!_registry.TryGet(id, out var definition))
            {
                refusal = new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId,
                    "Declared choices need the definition registered in this session; '" + id.LocalId + "' is not.");
                return false;
            }
            var undeclared = copy.Keys.FirstOrDefault(key => !definition.ChoiceKeys.Contains(key, StringComparer.Ordinal));
            if (undeclared != null)
            {
                refusal = new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId,
                    "Choice key '" + undeclared + "' is not declared by this definition.");
                return false;
            }
        }
        return true;
    }

    private StoryTransitionResult Retire(Lease lease, Guid expectedSessionId, Guid occurrenceId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices)
    {
        // Public input is validated before anything else happens, so an undefined outcome is a
        // refusal rather than an exception out of a method contracted to return a result.
        if (!Enum.IsDefined(typeof(StoryOutcome), outcome))
            return new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId, "Unknown outcome.");
        if (outcome == StoryOutcome.Completed)
            // A completion is the game's, recorded when the game is observed completing the mission.
            // Accepting one here would let a caller claim a completion for content that was never
            // accepted, or that the player abandoned in the game's own UI.
            return new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId,
                "A completion is recorded from the observed completion in the game, not declared here; declare an abandonment or a failure.");
        if (!ValidateChoices(lease, expectedSessionId, occurrenceId, choices, out var owned, out var choiceRefusal)) return choiceRefusal;
        if (!Guard(lease, expectedSessionId, out var guardRefusal, out var guardStatus))
            return new StoryTransitionResult(guardStatus, occurrenceId, guardRefusal);
        var caller = new StoryContentId(lease.ProviderId, LocalIdOf(occurrenceId));
        var terminalOwner = _ledger.ResolveOwned(caller, occurrenceId, out var terminal, out var terminalOwnership);
        if (terminalOwner != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(terminalOwner), occurrenceId, terminalOwnership);
        // The whole retirement is validated before the world is changed: choice bounds, encodability,
        // reservation and state. Discovering any of those after the mission was already abandoned in
        // the game would leave the world and the record disagreeing with no safe way back.
        // Choices declared earlier for this occurrence apply unless this call supplies its own.
        if (owned == null) _declaredChoices.TryGetValue(occurrenceId, out owned);
        var planned = _ledger.CanRetire(caller, occurrenceId, outcome, owned, out var plannedDetail);
        if (planned != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(planned), occurrenceId, plannedDetail);
        if (!BeginOperation(out var busy, occurrenceId)) return busy;
        string? worldRefusal;
        bool unavailable;
        // The removal this module performs raises the game's own mission events; the outcome below is
        // recorded by this operation, so the observer must not record a second one for it.
        _intent[occurrenceId] = outcome;
        try { worldRefusal = ReleaseInWorld(terminal!, outcome, out unavailable); }
        finally { _intent.Remove(occurrenceId); EndOperation(); }
        if (worldRefusal != null)
            return new StoryTransitionResult(unavailable ? StoryTransitionStatus.Unavailable : StoryTransitionStatus.InvalidTransition,
                occurrenceId, worldRefusal);
        // Re-validated after the world call, because consumer code ran inside it. A mission already
        // ended in the game cannot be un-ended without replaying its acceptance side effects, so a
        // refusal here blocks the module instead of pretending the two still agree.
        if (!Guard(lease, expectedSessionId, out var afterRefusal, out var afterStatus))
        {
            Fault("'" + occurrenceId + "' was ended in the world but its outcome could not be recorded: " + afterRefusal);
            return new StoryTransitionResult(afterStatus, occurrenceId,
                "The mission was ended in the game but the outcome could not be recorded; owned story content is blocked for this session.");
        }
        var committed = _ledger.Retire(caller, occurrenceId, outcome, owned, out var diagnostic);
        if (committed != StoryLedgerStatus.Accepted)
        {
            Fault("'" + occurrenceId + "' was ended in the world but its outcome was refused: " + diagnostic);
            return new StoryTransitionResult(Map(committed), occurrenceId,
                "The mission was ended in the game but the outcome was refused; owned story content is blocked for this session: " + diagnostic);
        }
        _declaredChoices.Remove(occurrenceId);
        ReleaseOccurrenceEntry(occurrenceId);
        return new StoryTransitionResult(StoryTransitionStatus.Accepted, occurrenceId, diagnostic);
    }

    /// <summary>
    /// Copies the caller's choices exactly once into the module's own dictionary. The collection is
    /// external input: its <c>Count</c> and <c>Keys</c> are not trusted, the enumeration is bounded at
    /// one item past the supported maximum so an endless sequence cannot hang the game, duplicate keys
    /// are refused rather than silently collapsed, and any failure of the caller's enumerator (including
    /// a real dictionary mutated on another thread) becomes a refusal with no mutation at all.
    /// Values are strings, so the copy is immutable once taken.
    /// </summary>
    private static bool TrySnapshotChoices(IReadOnlyDictionary<string, string> choices,
        out Dictionary<string, string> copy, out string refusal)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        copy = snapshot;
        refusal = "";
        try
        {
            using var pairs = choices.GetEnumerator();
            int read = 0;
            while (pairs.MoveNext())
            {
                if (++read > StoryMissionDefinition.MaxChoiceKeys)
                {
                    refusal = "At most " + StoryMissionDefinition.MaxChoiceKeys + " declared choices per occurrence.";
                    return false;
                }
                var pair = pairs.Current;
                if (pair.Key == null || pair.Value == null) { refusal = "A declared choice must be valid non-empty text."; return false; }
                if (snapshot.ContainsKey(pair.Key)) { refusal = "Duplicate declared choice key '" + pair.Key + "'."; return false; }
                snapshot.Add(pair.Key, pair.Value);
            }
        }
        catch (Exception error) when (Recoverable(error))
        {
            // The caller's collection failed to enumerate. That is a refusal, never a partially
            // recorded outcome and never an exception out of a method contracted to return a result.
            refusal = "The supplied declared choices could not be read (" + error.GetType().Name + "); nothing was recorded.";
            return false;
        }
        return true;
    }

    private static bool Recoverable(Exception error)
        => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    /// <summary>
    /// The local ID recorded for an occurrence, or a placeholder that belongs to no definition. It is
    /// only used to build the caller identity handed to the ledger, which then decides ownership; an
    /// unknown occurrence and a foreign one keep their own distinct refusals.
    /// </summary>
    private string LocalIdOf(Guid occurrenceId) => _ledger.TryGet(occurrenceId, out var entry) ? entry.Id.LocalId : "unknown";

    private StoryTransitionResult Transition(Lease lease, Guid expectedSessionId, Guid occurrenceId, LedgerCall call)
    {
        if (!Guard(lease, expectedSessionId, out var refusal, out var status)) return new StoryTransitionResult(status, occurrenceId, refusal);
        if (!_ledger.TryGet(occurrenceId, out var entry))
            return new StoryTransitionResult(StoryTransitionStatus.UnknownOccurrence, occurrenceId, "Unknown occurrence.");
        // The caller identity is the lease's own provider plus the occurrence's definition, so a
        // lease can only address content it owns.
        var id = new StoryContentId(lease.ProviderId, entry.Id.LocalId);
        var result = call(_ledger, id, out var diagnostic);
        return new StoryTransitionResult(Map(result), occurrenceId, diagnostic);
    }

    private static StoryTransitionStatus Map(StoryLedgerStatus status) => status switch
    {
        StoryLedgerStatus.Accepted => StoryTransitionStatus.Accepted,
        StoryLedgerStatus.UnknownOccurrence => StoryTransitionStatus.UnknownOccurrence,
        StoryLedgerStatus.ForeignOwner => StoryTransitionStatus.ForeignOwner,
        StoryLedgerStatus.LimitExceeded => StoryTransitionStatus.LimitExceeded,
        _ => StoryTransitionStatus.InvalidTransition
    };

    private bool TryDefinition(Lease lease, Guid expectedSessionId, string localId, out StoryContentId id,
        out StoryMissionDefinition? definition, out string refusal, out StoryTransitionStatus status)
    {
        definition = null;
        id = default;
        if (!Guard(lease, expectedSessionId, out refusal, out status)) return false;
        if (!StoryContentId.IsValidSegment(localId))
        {
            refusal = "A local ID is 1-48 lowercase ASCII letters/digits/hyphens starting with a letter.";
            status = StoryTransitionStatus.InvalidTransition;
            return false;
        }
        id = new StoryContentId(lease.ProviderId, localId);
        if (!_registry.TryGet(id, out var found))
        {
            refusal = "Definition '" + localId + "' is not registered by this provider.";
            status = StoryTransitionStatus.InvalidTransition;
            return false;
        }
        definition = found;
        return true;
    }

    /// <summary>
    /// The single precondition every mutation shares: an active lease, readable state for the current
    /// session, an owner that may be mutated right now, and the session the CALLER meant. The session
    /// check runs before any ledger or registry lookup, so a stale call cannot even observe what
    /// exists.
    ///
    /// The mutability check is deliberately separate from readability: while lifecycle callbacks
    /// dispatch or a save is in flight, reading is safe but accepting content is not, because it
    /// would not be part of the save that is already being written. That is a temporary refusal and
    /// is reported as such.
    /// </summary>
    private bool Guard(Lease lease, Guid expectedSessionId, out string refusal, out StoryTransitionStatus status)
    {
        status = StoryTransitionStatus.Unavailable;
        if (!lease.Active) { refusal = "This provider lease is no longer active."; return false; }
        var unavailable = Unavailable();
        if (unavailable != null)
        {
            refusal = "Story state is unavailable: " + unavailable + "; refusing to accept unsaved persistent content.";
            return false;
        }
        if (_persistence is not { MutationAllowed: true })
        {
            status = StoryTransitionStatus.Busy;
            refusal = "Story state is readable but mutations are paused while lifecycle callbacks dispatch or a save is in flight; "
                + "retry after the current operation, or the content would not be part of the save being written.";
            return false;
        }
        if (_fault != null)
        {
            refusal = "Owned story content is blocked for this session: " + _fault;
            return false;
        }
        if (_suspended != null)
        {
            refusal = "Owned story content is suspended for this session: " + _suspended;
            return false;
        }
        if (_operationInFlight)
        {
            status = StoryTransitionStatus.Busy;
            refusal = "Another owned-story operation is already running on this thread; retry after it completes.";
            return false;
        }
        if (expectedSessionId != _restoredSession)
        {
            // Occurrence identities are restored unchanged, so the same token addresses the same
            // occurrence after a reload. Only the session tells a delayed caller that the world it
            // observed is gone.
            status = StoryTransitionStatus.StaleSession;
            refusal = "This call expects a session that is not the loaded one; re-read the current state before mutating it.";
            return false;
        }
        refusal = "";
        return true;
    }

    private StoryOccurrenceQuery Occurrences(Lease lease, string localId)
    {
        var unavailable = QueryRefusal(lease, localId);
        if (unavailable != null) return new StoryOccurrenceQuery(StoryKnowledge.Unavailable, null, null, unavailable);
        return new StoryOccurrenceQuery(StoryKnowledge.Known, _restoredSession,
            _ledger.Retained(new StoryContentId(lease.ProviderId, localId)), "restored state of the current session");
    }

    private StoryOccurrenceSnapshotQuery Unresolved(Lease lease, string localId)
    {
        var unavailable = QueryRefusal(lease, localId);
        if (unavailable != null) return new StoryOccurrenceSnapshotQuery(StoryKnowledge.Unavailable, null, null, unavailable);
        return new StoryOccurrenceSnapshotQuery(StoryKnowledge.Known, _restoredSession,
            _ledger.Unresolved(new StoryContentId(lease.ProviderId, localId)), "unresolved occurrences of the current session");
    }

    private StoryCompletionQuery IsCompleted(Lease lease, string localId)
    {
        var unavailable = QueryRefusal(lease, localId);
        if (unavailable != null) return new StoryCompletionQuery(StoryKnowledge.Unavailable, null, null, unavailable);
        return new StoryCompletionQuery(StoryKnowledge.Known, _restoredSession,
            _ledger.IsCompleted(new StoryContentId(lease.ProviderId, localId)), "campaign completion of the current session");
    }

    private string? QueryRefusal(Lease lease, string localId)
    {
        if (!lease.Active) return "this provider lease is no longer active";
        if (!StoryContentId.IsValidSegment(localId)) return "the local ID is not a valid identity segment";
        return Unavailable();
    }

    /// <summary>
    /// Releases a provider's registrations. Only the BASE catalog entries go: an occurrence the player
    /// is still holding keeps its own entry, so the mission stays addressable and its outcome can
    /// still be observed and recorded after the lease that offered it is gone.
    /// </summary>
    private void ReleaseProvider(Lease lease)
    {
        if (_disposed) return;
        foreach (var identifier in _registry.IdentifiersOf(lease.ProviderId)) _world?.Uninstall(identifier);
        _registry.RemoveProvider(lease.ProviderId);
        if (_leasesBySegment.TryGetValue(lease.ProviderId, out var current) && ReferenceEquals(current, lease))
            _leasesBySegment.Remove(lease.ProviderId);
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        // Only the module's own shutdown unregisters the persistence owner.
        _lifecycle?.Dispose();
        _persistence?.Dispose();
        _leasesBySegment.Clear();
        _bindings.Clear();
        foreach (var identifier in _registry.Identifiers()) _world?.Uninstall(identifier);
        foreach (var identifier in _occurrenceIdentifiers.Values.ToArray()) _world?.Uninstall(identifier);
        _occurrenceIdentifiers.Clear();
        _deferredUninstall.Clear();
        _declaredChoices.Clear();
        _missionObserver?.Dispose();
        _registry.Clear();
        _registry.ResetWorldReservations();
        _ledger.Reset();
        _readiness = Readiness.None;
        _readinessDetail = "the story module is disposed";
    }
}

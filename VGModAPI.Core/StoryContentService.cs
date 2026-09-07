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
            return _service.Transition(this, expectedSessionId, occurrenceId,
                (StoryLedger ledger, StoryContentId id, out string diagnostic) => ledger.Activate(id, occurrenceId, out diagnostic));
        }

        public StoryTransitionResult Withdraw(Guid expectedSessionId, Guid occurrenceId)
        {
            _service.CheckThread();
            return _service.Transition(this, expectedSessionId, occurrenceId,
                (StoryLedger ledger, StoryContentId id, out string diagnostic) => ledger.Withdraw(id, occurrenceId, out diagnostic));
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
    private readonly IPersistenceRegistration? _persistence;
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
        Func<Guid>? newOccurrence = null, Action? checkThread = null)
    {
        checkThread?.Invoke();
        if (lifecycle?.CurrentSession != null)
            throw new InvalidOperationException("The story module must be constructed before a session begins.");
        _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
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
        if (_persistence is not { StateReady: true }) return "story persistence is " + PersistenceStatus;
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
            _service._registry.RemoveIfMatches(Id, _entry);
        }
    }

    private StoryTransitionResult Offer(Lease lease, Guid expectedSessionId, string localId)
    {
        if (!TryDefinition(lease, expectedSessionId, localId, out var id, out var definition, out var refusal, out var status))
            return new StoryTransitionResult(status, Guid.Empty, refusal);
        var occurrenceId = _newOccurrence();
        // The outcome's worst-case payload is reserved now, so this occurrence can always be retired.
        var result = _ledger.Offer(id, definition!.Retention, occurrenceId, definition.ReservedChoiceBytes, out var diagnostic);
        return new StoryTransitionResult(Map(result), result == StoryLedgerStatus.Accepted ? occurrenceId : Guid.Empty, diagnostic);
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
    private StoryTransitionResult Retire(Lease lease, Guid expectedSessionId, Guid occurrenceId, StoryOutcome outcome,
        IReadOnlyDictionary<string, string>? choices)
    {
        Dictionary<string, string>? owned = null;
        if (choices != null)
        {
            // Availability, lease and session are checked BEFORE anything is looked up, and ownership
            // is resolved by the ledger's own rules, so this path can never answer differently from
            // the choice-free path or mention another provider's local ID. Reading a foreign caller's
            // collection is itself deferred until after that authorisation.
            if (!Guard(lease, expectedSessionId, out var refusal, out var status))
                return new StoryTransitionResult(status, occurrenceId, refusal);
            var resolved = _ledger.ResolveOwned(new StoryContentId(lease.ProviderId, LocalIdOf(occurrenceId)), occurrenceId,
                out var entry, out var ownership);
            if (resolved != StoryLedgerStatus.Accepted) return new StoryTransitionResult(Map(resolved), occurrenceId, ownership);
            if (!TrySnapshotChoices(choices, out owned, out var unreadable))
                return new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId, unreadable);
            // Reading the caller's collection ran ITS code on this thread, which may have disposed the
            // lease, retired this occurrence or reloaded the save. Authorisation is therefore
            // re-established before anything else is consulted, so a caller that invalidated itself
            // hears that, not a diagnostic about the state it just changed.
            if (!Guard(lease, expectedSessionId, out refusal, out status))
                return new StoryTransitionResult(status, occurrenceId, refusal);
            if (owned.Count > 0)
            {
                var id = new StoryContentId(lease.ProviderId, entry!.Id.LocalId);
                if (!_registry.TryGet(id, out var definition))
                    return new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId,
                        "Declared choices need the definition registered in this session; '" + id.LocalId + "' is not.");
                var undeclared = owned.Keys.FirstOrDefault(key => !definition.ChoiceKeys.Contains(key, StringComparer.Ordinal));
                if (undeclared != null)
                    return new StoryTransitionResult(StoryTransitionStatus.InvalidTransition, occurrenceId,
                        "Choice key '" + undeclared + "' is not declared by this definition.");
            }
        }
        return Transition(lease, expectedSessionId, occurrenceId, (StoryLedger ledger, StoryContentId id, out string diagnostic)
            => ledger.Retire(id, occurrenceId, outcome, owned, out diagnostic));
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

    private void ReleaseProvider(Lease lease)
    {
        if (_disposed) return;
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
        _registry.Clear();
        _registry.ResetWorldReservations();
        _ledger.Reset();
        _readiness = Readiness.None;
        _readinessDetail = "the story module is disposed";
    }
}

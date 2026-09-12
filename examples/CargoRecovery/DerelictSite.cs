using System;
using VGModAPI;

namespace CargoRecovery;

/// <summary>
/// Authors the boarding target this example recovers cargo from, so the mod supplies its own content
/// instead of waiting for the player to find a suitable vanilla derelict.
///
/// Dungeon content attaches to *existing* targets - world creation is a separate service - so this
/// composes the two:
///
///   1. an owned SALVAGE site in the player's current system, declared `withStation: true`, which
///      guarantees a native derelict station (research / relay / industrial). Guaranteed, not a roll.
///   2. that station's installation is held enterable, so ambient world damage cannot invalidate the
///      objective before the player gets there, and
///   3. the cargo encounter attaches by installation identity once a live target is observed.
///
/// Teardown is the site's own `Remove()`: no pocket system, no extra gate and no travel leg. The
/// player's system is left exactly as it was found.
///
/// Public contracts only - no Unity, BepInEx or native type.
/// </summary>
public sealed class DerelictSite : IDisposable
{
    // A derelict station requires salvage level 5 or higher.
    private const int StationLevel = 5;
    private const string SiteDef = "derelict-site";
    private const string SiteName = "Abandoned Freight Station";

    // System-local placement, kept clear of the system centre so the station does not land on top of
    // whatever the system already holds.
    private const float OffsetX = 45f;
    private const float OffsetY = 30f;

    private readonly IWorldProvider _world;
    private readonly Func<CargoEncounter?> _encounter;
    private readonly Action<string> _log;

    private const string SiteKey = "derelict-station";

    private IResourceSite? _site;
    private IDungeonInstallation? _installation;
    private IDisposable? _enterable;
    private bool _removalRequested;

    /// <summary>
    /// Construct BEFORE a session exists. World declarations are refused with
    /// <see cref="WorldStatus.NotReady"/> once a session is running, so registering lazily on first
    /// use silently leaves nothing to create. Registering declares content; it never creates a
    /// native object, so doing it early costs nothing.
    /// </summary>
    public DerelictSite(IWorldProvider world, Func<CargoEncounter?> encounter, Action<string> log)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _encounter = encounter ?? throw new ArgumentNullException(nameof(encounter));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        Declare("salvage site", _world.RegisterResourceSite(ResourceSiteDefinition.Salvage(SiteDef, 1, SiteName, StationLevel,
            wreckShipId: "Monsoon", factionId: "Fanatics", withStation: true, hazard: null, scatterAsteroids: true)));
    }

    /// <summary>Never ignore a declaration result: a refused declaration cannot create anything later.</summary>
    private void Declare(string what, WorldStatus status)
    {
        if (status == WorldStatus.Succeeded) return;
        _log($"Derelict {what} declaration refused: {status}"
            + (status == WorldStatus.NotReady ? " (world content must be declared before a session starts)." : "."));
    }

    public bool Exists => _site != null && _site.State.Status != ReconstructionStatus.Removed;
    public string SiteState => _site == null ? "-" : _site.State.Status.ToString();
    public string? StationPoiId => _site?.PoiId;
    /// <summary>The authored station's installation, once its native POI exists.</summary>
    public IDungeonInstallation? Installation { get { HoldEnterable(); return _installation; } }

    /// <summary>Creates the derelict station in the system the player is already in.</summary>
    public bool Spawn(string systemId)
    {
        if (Exists) return true;
        _site = _world.CreateResourceSite(SiteDef, SiteKey, systemId, OffsetX, OffsetY);
        if (_site == null)
        { _log("Could not create the derelict (declaration refused earlier, or the world cannot author right now)."); return false; }

        HoldEnterable();
        _log($"Derelict ready in this system. Station POI: {_site.PoiId ?? "pending"}.");
        return true;
    }

    /// <summary>
    /// Keeps the authored station enterable while held, so ambient damage cannot destroy its docking
    /// or collapse its interior before the player arrives. Unrelated stations stay entirely vanilla.
    /// </summary>
    private void HoldEnterable()
    {
        if (_site?.PoiId is not string poi || _installation != null) return;
        if (_encounter() is not CargoEncounter encounter) return;
        _installation = encounter.GetInstallation(poi);
        _enterable = _installation.KeepEnterable();
    }

    /// <summary>
    /// Full cleanup: removing the site takes its native POI, its derelict station and its save row,
    /// and drops the attached cargo occurrence.
    ///
    /// `Remove()` is the PLAIN native removal - it refuses only on integrity grounds and deliberately
    /// does NOT check transient player-safety conditions, so calling it blind could tear the station
    /// out from under a player who is docked at or boarding it. Ask `CanRemove()` first, and when the
    /// answer is not Ready hand the job to `RequestRemoval()`, which mirrors the game's own ambient
    /// cleanup window and completes as soon as the condition clears.
    ///
    /// The enterable hold is released first: while it is held, readiness reports HeldEnterable and
    /// removal would never become possible. It is deliberately NOT re-acquired on a deferral, since
    /// that would block the deferred removal forever.
    /// </summary>
    public bool Remove()
    {
        if (_site == null) return true;
        ReleaseHold();

        var readiness = _site.CanRemove();
        if (readiness == WorldContentRemovalStatus.Ready)
        {
            var result = _site.Remove();
            if (result.Succeeded)
            {
                _site = null;
                _log("Derelict removed: station, site and its cargo attachment are gone.");
                return true;
            }
            _log("Derelict removal refused: " + result.Status + " - " + result.Detail);
            HoldEnterable();   // still ours and still protected; nothing was deferred
            return false;
        }
        if (readiness == WorldContentRemovalStatus.NotPresent) { _site = null; return true; }

        var deferred = _site.RequestRemoval();
        _removalRequested = deferred.Succeeded;
        _log($"Derelict cannot be removed yet ({readiness}); "
            + (deferred.Succeeded
                ? "queued for the next safe cleanup window - it disappears once you are clear of it, but only within this session."
                : "deferral refused: " + deferred.Status + " - " + deferred.Detail));
        return false;
    }

    private void ReleaseHold()
    { var hold = _enterable; _enterable = null; hold?.Dispose(); _installation = null; }

    /// <summary>
    /// Drops handles belonging to a session that has ended. An occurrence from an ended session keeps
    /// its last observed state forever and never resolves against the replacement save, so holding it
    /// would make the HUD report a derelict that no longer exists.
    /// </summary>
    public void ForgetSession()
    {
        ReleaseHold();
        _site = null;
    }

    /// <summary>
    /// Re-obtains the occurrence for the live game after a load. The API restored it from save data;
    /// this only re-acquires a handle to it, and creates nothing.
    ///
    /// A queued <see cref="IResourceSite.RequestRemoval"/> does NOT survive session replacement: the
    /// deferred pass only completes within the session that requested it. So a pending removal is
    /// reported as abandoned and has to be re-issued, rather than being silently assumed done.
    /// </summary>
    public void Reacquire()
    {
        _site = _world.GetResourceSite(SiteDef, SiteKey);
        if (_site == null)
        {
            if (_removalRequested) _log("Queued derelict removal completed before the session ended.");
            _removalRequested = false;
            return;
        }
        HoldEnterable();
        if (_removalRequested)
        {
            _removalRequested = false;
            _log("The queued derelict removal was abandoned when the session was replaced; press Remove derelict again.");
        }
    }

    public void Dispose() => ReleaseHold();
}

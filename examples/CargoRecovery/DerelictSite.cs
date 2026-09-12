using System;
using VGModAPI;

namespace CargoRecovery;

/// <summary>
/// Authors the boarding target this example recovers cargo from, so the mod supplies its own
/// content instead of waiting for the player to find a suitable vanilla derelict.
///
/// Dungeon content attaches to *existing* targets — world creation is a separate service — so this
/// composes the two:
///
///   1. an owned pocket system anchored to the player's current system, with its gate opened, and
///   2. an owned SALVAGE site inside it declared `withStation: true`, which guarantees a native
///      derelict station (research / relay / industrial). Guaranteed, not a probability roll.
///   3. that station's installation is held enterable, so ambient world damage cannot invalidate
///      the objective before the player arrives, and
///   4. the cargo encounter attaches by installation identity once a live target is observed.
///
/// The site lives inside the pocket so a single `Dissolve()` removes the station, the site and the
/// system together: authored sites have no `Dissolve` of their own, they go with their pocket.
///
/// Public contracts only — no Unity, BepInEx or native type.
/// </summary>
public sealed class DerelictSite : IDisposable
{
    // A derelict station requires salvage level 5 or higher.
    private const int StationLevel = 5;
    private const string PocketDef = "derelict-pocket";
    private const string SiteDef = "derelict-site";
    private const string PocketName = "Salvage Approach";
    private const string SiteName = "Abandoned Freight Station";

    private readonly IWorldProvider _world;
    private readonly CargoEncounter _encounter;
    private readonly Action<string> _log;

    private IPocketSystem? _pocket;
    private IResourceSite? _site;
    private IDungeonInstallation? _installation;
    private IDisposable? _enterable;

    public DerelictSite(IWorldProvider world, CargoEncounter encounter, Action<string> log)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _encounter = encounter ?? throw new ArgumentNullException(nameof(encounter));
        _log = log ?? throw new ArgumentNullException(nameof(log));

        // Registering declares content; it never creates a native object.
        _world.RegisterPocketSystem(new PocketSystemDefinition(PocketDef, 1, PocketName,
            PocketSystemPlacement.Visible, factionId: null, sectorName: null, quiet: true));
        _world.RegisterResourceSite(ResourceSiteDefinition.Salvage(SiteDef, 1, SiteName, StationLevel,
            wreckShipId: "Monsoon", factionId: "Fanatics", withStation: true, hazard: null, scatterAsteroids: true));
    }

    public bool Exists => _pocket != null;
    public string PocketState => _pocket == null ? "-" : _pocket.State.Status.ToString();
    public string SiteState => _site == null ? "-" : _site.State.Status.ToString();
    public string? StationPoiId => _site?.PoiId;
    /// <summary>The authored station's installation, once its native POI exists.</summary>
    public IDungeonInstallation? Installation { get { HoldEnterable(); return _installation; } }

    /// <summary>Creates the pocket, the derelict station inside it, and holds it enterable.</summary>
    public bool Spawn(string anchorSystemId)
    {
        if (_pocket != null) return true;
        _pocket = _world.CreatePocketSystem(PocketDef, "derelict", anchorSystemId);
        if (_pocket?.SystemId == null) { _log("Could not create the approach system."); _pocket = null; return false; }

        // Open the anchored gate: this is the way in from the player's own system.
        _pocket.SetEntranceOpen(true);

        _site = _world.CreateResourceSite(SiteDef, "derelict-station", _pocket.SystemId, 0f, 0f);
        if (_site == null) { _log("Could not create the salvage site; removing the empty system."); Remove(); return false; }

        HoldEnterable();
        _log($"Derelict ready in '{PocketName}' (gate from your system). Station POI: {_site.PoiId ?? "pending"}.");
        return true;
    }

    /// <summary>
    /// Keeps the authored station enterable while held, so ambient damage cannot destroy its docking
    /// or collapse its interior before the player arrives. Unrelated stations stay entirely vanilla.
    /// </summary>
    private void HoldEnterable()
    {
        if (_site?.PoiId is not string poi || _installation != null) return;
        _installation = _encounter.GetInstallation(poi);
        _enterable = _installation.KeepEnterable();
    }

    /// <summary>Full cleanup: the station and the site go with the pocket that holds them.</summary>
    public bool Remove()
    {
        ReleaseHold();
        if (_pocket == null) return true;
        var result = _pocket.Dissolve();
        if (!result.Succeeded) { _log("Derelict removal refused: " + result.Status + " - " + result.Detail); return false; }
        _pocket = null; _site = null;
        _log("Derelict removed: station, site and system are gone.");
        return true;
    }

    private void ReleaseHold()
    { var hold = _enterable; _enterable = null; hold?.Dispose(); _installation = null; }

    public void Dispose() => ReleaseHold();
}

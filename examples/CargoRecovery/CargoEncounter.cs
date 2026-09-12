using System;
using System.Collections.Generic;
using VGModAPI;

namespace CargoRecovery;

/// <summary>
/// The authored encounter itself: a compartment layout, a discovered cargo-room choice and
/// automatically persisted occurrence state. Public contracts only — no Unity, BepInEx, Harmony,
/// private API cast, serializer or reconstruction scheduler.
/// Call from a BepInEx consumer on the main thread after checking dungeon-content availability.
/// </summary>
public sealed class CargoEncounter : IDisposable
{
    private readonly IDungeonProvider _provider;
    private readonly IDisposable _definition;
    public CargoEncounter(IDungeonContentService content, string pluginId, string existingRewardItemId)
    {
        _provider = content.AcquireProvider(pluginId);
        try { _definition = _provider.Register("cargo-recovery", Definition(existingRewardItemId)); }
        catch { _provider.Dispose(); throw; }
    }
    public static DungeonDefinition Definition(string existingRewardItemId) => new(1, "Cargo recovery", new DungeonLayout(new[]
    {
        new DungeonCompartmentDefinition("entry", CompartmentType.Airlock, new[] { "hold" }),
        new DungeonCompartmentDefinition("hold", CompartmentType.CargoHold, new[] { "entry", "control" },
            defenders: new Dictionary<string, int> { ["Marine"] = 1 }),
        new DungeonCompartmentDefinition("control", CompartmentType.ControlRoom, new[] { "hold" }, locked: true)
    }), events: new[]
    {
        new DungeonEventDefinition("recover", "hold", "The cargo is secured. Recover the shipment or leave it aboard.", new[]
        {
            new DungeonChoiceDefinition("recover", "Recover shipment", "Marine", new[] { new DungeonLootDefinition(existingRewardItemId, 2) }),
            new DungeonChoiceDefinition("leave", "Leave shipment")
        })
    }, allowHazards: false, allowScheduledReinforcements: false);
    public DungeonContentResult Attach(BoardingHandle observedTarget) => _provider.Attach("cargo-recovery", observedTarget);
    /// <summary>
    /// Attaches by persistent installation identity instead of a live target handle — the form to use
    /// for a station this mod authored itself, because the installation exists before any boarding
    /// target is observed. While no live target belongs to it yet (you are not there), the result is
    /// a temporary <see cref="DungeonContentStatus.StaleTarget"/> refusal, not a failure.
    /// </summary>
    public DungeonContentResult Attach(IDungeonInstallation installation) => _provider.Attach("cargo-recovery", installation);
    /// <summary>A stable view of a POI's installation, valid before its native POI exists.</summary>
    public IDungeonInstallation GetInstallation(string poiId) => _provider.GetInstallation(poiId);
    public DungeonContentResult Recover(Guid occurrence) => _provider.Choose(occurrence, "recover", "recover");
    public DungeonContentResult Leave(Guid occurrence) => _provider.Choose(occurrence, "recover", "leave");
    public IReadOnlyList<DungeonOccurrenceSnapshot> SavedOccurrences => _provider.GetOccurrences();
    public void Dispose() { _definition.Dispose(); _provider.Dispose(); }
}

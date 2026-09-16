using System;

namespace VGModAPI;

/// <summary>Read-only tractor-module values captured for one configuration or tooltip callback.</summary>
public sealed class TractorModule
{
    /// <summary>Vanilla automatic tractor beams (amountOfBeams).</summary>
    public int BeamCount { get; }
    /// <summary>Manual Tractor Beams in the game's UI (amountOfBonusBeams).</summary>
    public int ManualBeamCount { get; }
    internal TractorModule(int beamCount, int manualBeamCount)
    { BeamCount = beamCount; ManualBeamCount = manualBeamCount; }
}

/// <summary>Changes targeting capacity, not the number of physical beams or their native eligibility rules.</summary>
public sealed class TractorTargeting
{
    public int AutomaticBeamLimit { get; }
    public bool AllowManualBorrowing { get; }
    public TractorTargeting(int automaticBeamLimit, bool allowManualBorrowing = false)
    {
        if (automaticBeamLimit < 0) throw new ArgumentOutOfRangeException(nameof(automaticBeamLimit));
        AutomaticBeamLimit = automaticBeamLimit; AllowManualBorrowing = allowManualBorrowing;
    }
}

public interface IEquipmentService : IServiceStatus
{
    /// <summary>Configure player-ship tractor targeting. Return null for vanilla behavior. The first non-null
    /// configuration wins in registration order. Evaluated synchronously when the module needs it, with current
    /// module values. Exceptions abstain; gameplay mutation and recursive evaluation are not allowed.
    /// Disposal removes the rule. Native cargo/crew/brig checks and occupied beams remain protected.</summary>
    IDisposable ConfigurePlayerTractorModules(string pluginId, Func<TractorModule, TractorTargeting?> configure);
}

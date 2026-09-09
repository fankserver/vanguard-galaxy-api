using System;
using VGModAPI;

namespace ServiceConsumers;

/// <summary>Subscribe once before a campaign creates its native station POIs; no update or session-reset loop.</summary>
public sealed class InstallationStory : IDisposable
{
    private readonly IDungeonProvider _dungeons;

    public InstallationStory(IDungeonContentService dungeons, string pluginId, ISaveDataRegistration customSaveData,
        Action stationAStoryBeat, Action stationBStoryBeat)
    {
        _dungeons = dungeons.AcquireProvider(pluginId, saveData: customSaveData);
        _dungeons.GetInstallation("MyCampaignStationA").ExtractionStarted += stationAStoryBeat;
        _dungeons.GetInstallation("MyCampaignStationB").ExtractionStarted += stationBStoryBeat;
    }

    public void Dispose() => _dungeons.Dispose();
}

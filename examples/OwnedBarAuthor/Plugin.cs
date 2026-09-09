using System;
using BepInEx;
using VGModAPI;

namespace OwnedBarAuthor;

[BepInPlugin(Id, "Owned bar author example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.32")]
public sealed class Plugin : BaseUnityPlugin
{
#if BAR_AUTHOR_B
    public const string Id = "vg-bar-author-b";
#else
    public const string Id = "vg-bar-author-a";
#endif
    private IBarProvider? _provider;
    private IStoryProvider? _story;
    public IStoryProvider StoryProvider => _story ?? throw new InvalidOperationException("Register the linked story first.");
    public const string LinkedStoryId = "linked-mission";

    public StoryRegistrationResult RegisterStory(string faction)
    {
        if (_story == null)
            _story = (ModApi.Services.Story)
                .AcquireProvider(this).Provider ?? throw new InvalidOperationException("Story authentication refused.");
        return _story.Register(new StoryMissionDefinition(LinkedStoryId, "Linked contact", "A retained contact and mission",
            new StoryFactionId(faction), new[] { new StoryStep("Speak to the contact",
                new[] { StoryObjective.Scripted("talk", "Speak twice", 2) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 1) }));
    }

    public BarResult RegisterLinked(string station, string local, string seed, Guid occurrence)
    {
        return AcquireBar().Register(new BarPatronDefinition(local, station, "Linked contact " + Id,
            "Mission-dependent presentation", seed, BarPatronRetention.Persistent,
            new StoryContentId(StoryProvider.ProviderId, LinkedStoryId), occurrence), _ => Interactions++);
    }

    public void ReleaseStory() { _story?.Dispose(); _story = null; }
    public IBarProvider Provider => _provider ?? throw new InvalidOperationException("Register the author first.");
    public int Interactions { get; private set; }

    private IBarProvider AcquireBar()
    {
        if (_provider == null)
            _provider = (ModApi.Services.Bars)
                .AcquireProvider(this).Provider ?? throw new InvalidOperationException("Author authentication refused.");
        return _provider;
    }

    public BarResult Register(string station, string local, string seed) =>
        AcquireBar().Register(new BarPatronDefinition(local, station, "Contact " + Id,
            "Independently authored contact", seed, BarPatronRetention.Persistent), _ => Interactions++);

    public BarResult Configure(string station, BarRosterOwnership mode)
        => Provider.ConfigureStation(station, mode);
    public BarResult Place(Guid session, string local) => Provider.Place(session, local);
    public void Release() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() { Release(); ReleaseStory(); }
}

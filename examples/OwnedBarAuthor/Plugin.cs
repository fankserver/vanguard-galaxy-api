using System;
using BepInEx;
using VGModAPI;

namespace OwnedBarAuthor;

[BepInPlugin(Id, "Owned bar author example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.2.0")]
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
            new[] { StoryReward.Credits(1) }));
    }

    public BarRegistrationResult RegisterLinked(string station, string local, string seed)
    {
        return AcquireBar().Register(new BarPatronDefinition(local, station, "Linked contact " + Id,
            "Mission-dependent presentation", seed, BarPatronRetention.Persistent,
            new StoryContentId(StoryProvider.ProviderId, LinkedStoryId)), _ => Interactions++);
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

    public BarRegistrationResult Register(string station, string local, string seed) =>
        AcquireBar().Register(new BarPatronDefinition(local, station, "Contact " + Id,
            "Independently authored contact",
            new BarPatronPresentation(seed, CharacterPortrait.Named("MercWoman"), isMale: false)), _ => Interactions++);

    public BarResult Configure(string station, BarRosterOwnership mode)
        => Provider.ConfigureStation(station, mode);
    public void Release() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() { Release(); ReleaseStory(); }
}

using System;
using BepInEx;
using VGModAPI;

namespace OwnedStoryJob;

[BepInPlugin("vg-story-job", "Owned generated job example", "0.1.0")]
[BepInDependency(ModApi.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string LocalId = "mission-x";
    private IStoryProvider? _provider;
    public IStoryProvider Provider => _provider ?? throw new InvalidOperationException("Story provider is not acquired.");

    // A generated pitch supplies content, not a serializer or restoration callback. Reusing this
    // local ID in the independently loaded campaign assembly does not share ownership.
    public StoryRegistrationResult Register(string title, string description, string destinationPoiId,
        string sourceFaction, int rewardCredits)
    {
        if (_provider == null)
        {
            var api = ModApi.Story ?? throw new InvalidOperationException("Enable the optional story module.");
            var acquired = api.AcquireProvider(this);
            _provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
        }
        return _provider.Register(new StoryMissionDefinition(LocalId, title, description,
            new StoryFactionId(sourceFaction),
            new[] { new StoryStep("Visit the delivery point", new[] { StoryObjective.TravelTo(destinationPoiId) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, rewardCredits) },
            retention: StoryRetention.Temporary));
    }

    public void ReleaseProvider() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() => ReleaseProvider();
}

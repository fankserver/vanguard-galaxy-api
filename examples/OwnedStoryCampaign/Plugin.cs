using System;
using BepInEx;
using VGModAPI;

namespace OwnedStoryCampaign;

[BepInPlugin("vg-story-campaign", "Owned story campaign example", "0.1.0")]
[BepInDependency("vgmodapi", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string LocalId = "mission-x";
    private IStoryProvider? _provider;
    public IStoryProvider Provider => _provider ?? throw new InvalidOperationException("Story provider is not acquired.");

    // Called by the demonstration UI/driver after choosing an existing destination. No save hooks:
    // the API retains the definition, occurrences, progress and declared campaign choices.
    public StoryRegistrationResult Register(string destinationPoiId, string sourceFaction)
    {
        if (_provider == null)
        {
            var api = ModApi.Story ?? throw new InvalidOperationException("Enable the optional story module.");
            var acquired = api.AcquireProvider(this);
            _provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
        }
        return _provider.Register(new StoryMissionDefinition(LocalId,
            "A promise kept", "Visit the rendezvous, then report to the witness.",
            new StoryFactionId(sourceFaction),
            new[]
            {
                new StoryStep("Reach the rendezvous", new[] { StoryObjective.TravelTo(destinationPoiId) }),
                new StoryStep("Report to the witness", new[] { StoryObjective.TravelTo(destinationPoiId) })
            }, new[] { new StoryReward(StoryRewardKind.Credits, 17) },
            retention: StoryRetention.Campaign, choiceKeys: new[] { "witness" }));
    }

    public void ReleaseProvider() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() => ReleaseProvider();
}

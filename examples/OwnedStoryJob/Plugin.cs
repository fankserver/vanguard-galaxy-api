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
            var api = ModApi.Services.Story ?? throw new InvalidOperationException("Enable the optional story module.");
            var acquired = api.AcquireProvider(this);
            _provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
        }
        return _provider.Register(new StoryMissionDefinition(LocalId, title, description,
            new StoryFactionId(sourceFaction),
            new[] { new StoryStep("Visit the delivery point", new[] { StoryObjective.TravelTo(destinationPoiId) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, rewardCredits) },
            retention: StoryRetention.Temporary));
    }

    public const string ObjectiveLocalId = "objective-x";

    // Generated text and progress requirements use the same owner-scoped API as authored content.
    public StoryRegistrationResult RegisterObjectives(string sourceFaction, string generatedDescription, int requiredReports)
    {
        if (_provider == null)
        {
            var api = ModApi.Services.Story ?? throw new InvalidOperationException("Enable the optional story module.");
            var acquired = api.AcquireProvider(this);
            _provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
        }
        return _provider.Register(new StoryMissionDefinition(ObjectiveLocalId, "Generated field report", generatedDescription,
            new StoryFactionId(sourceFaction), new[] { new StoryStep("Collect reports",
                new[] { StoryObjective.Scripted("talk", generatedDescription, requiredReports) }) },
            new[] { new StoryReward(StoryRewardKind.Credits, 3) }, retention: StoryRetention.Temporary));
    }

    public StoryTransitionResult ReportProgress(Guid session, Guid occurrence, int observedReports)
        => ((IStoryObjectiveProvider)Provider).SetProgress(session,
            new StoryObjectiveId(new StoryContentId(Provider.ProviderId, ObjectiveLocalId), occurrence, "talk"), observedReports);

    public void ReleaseProvider() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() => ReleaseProvider();
}

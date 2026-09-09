using System;
using BepInEx;
using VGModAPI;

namespace OwnedStoryCampaign;

[BepInPlugin("vg-story-campaign", "Owned story campaign example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.2.0")]
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
            var api = ModApi.Services.Story;
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

    public const string ObjectiveLocalId = "objective-x";

    // This authored beat declares only objective state; the provider owns no save/load hooks.
    public StoryRegistrationResult RegisterObjectives(string sourceFaction, int revision = 1)
    {
        if (_provider == null)
        {
            var api = ModApi.Services.Story;
            var acquired = api.AcquireProvider(this);
            _provider = acquired.Provider ?? throw new InvalidOperationException(acquired.Diagnostic);
        }
        var talk = new StoryStep("Listen to the witness", new[] { StoryObjective.Scripted("talk", "Hear the witness", 3) });
        var report = new StoryStep("Choose an answer", new[] { StoryObjective.Scripted("report", "Promise to investigate") });
        var definition = new StoryMissionDefinition(ObjectiveLocalId, "A witness's account", "Listen, then choose your reply.",
            new StoryFactionId(sourceFaction), revision == 1 ? new[] { talk, report } : new[] { report, talk },
            new[] { new StoryReward(StoryRewardKind.Credits, 7) }, retention: StoryRetention.Campaign);
        return _provider.Register(revision == 1 ? definition : definition.WithRevision(revision, 1));
    }

    public StoryRegistrationResult RegisterObservedObjectives(string sourceFaction, string destination)
        => Provider.Register(new StoryMissionDefinition("observed-x", "Observe native progress", "Read the game's own objective state.",
            new StoryFactionId(sourceFaction), new[] {
                new StoryStep("Keep credits", new[] { StoryObjective.CollectCredits(StoryObjective.MaxAmount).WithKey("credits") }),
                new StoryStep("Visit the destination", new[] { StoryObjective.TravelTo(destination).WithKey("visit") }) },
            retention: StoryRetention.Temporary));

    public StoryRegistrationResult RegisterChangedObservedObjectives(string sourceFaction)
        => Provider.Register(new StoryMissionDefinition("observed-x", "Replacement startup title", "Replacement startup description",
            new StoryFactionId(sourceFaction), new[] {
                new StoryStep("Different credits", new[] { StoryObjective.CollectCredits(101).WithKey("credits") }),
                new StoryStep("Different destination", new[] { StoryObjective.TravelTo("missing-replacement-poi").WithKey("visit") }) },
            retention: StoryRetention.Temporary));

    // Invoked by the consumer's conversation controller when the authored answer is chosen.
    public StoryTransitionResult AnswerWitness(Guid session, Guid occurrence)
        => ((IStoryObjectiveProvider)Provider).SetProgress(session,
            new StoryObjectiveId(new StoryContentId(Provider.ProviderId, ObjectiveLocalId), occurrence, "report"), 1);

    public void ReleaseProvider() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() => ReleaseProvider();
}

using System;
using VGModAPI;

namespace ServiceConsumers;

/// <summary>Definitions are registered by the plugin; reactions receive the game that produced the event.</summary>
public sealed class StoryReactions : IDisposable
{
    private readonly IStoryDefinition _intro;
    private readonly IStoryDefinition _followup;
    public StoryReactions(IStoryDefinition intro, IStoryDefinition followup)
    {
        _intro = intro ?? throw new ArgumentNullException(nameof(intro));
        _followup = followup ?? throw new ArgumentNullException(nameof(followup));
        _intro.Completed += OnCompleted;
    }
    public IStoryMission OfferIntroduction(IGame game) => game.Story.Offer(_intro);
    private void OnCompleted(IStoryMission mission) => mission.Game.Story.Offer(_followup);
    public void Dispose() => _intro.Completed -= OnCompleted;
}

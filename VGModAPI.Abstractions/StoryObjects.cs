using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum StoryMissionState { Offering, Offered, Active, Completed, Failed, Abandoned, Withdrawn, Unavailable, GameEnded }
public enum StoryActionStatus { Queued, Succeeded, Rejected, Unavailable, GameEnded }

public sealed class StoryActionResult
{
    private StoryActionStatus _status;
    private readonly Func<StoryActionStatus?>? _ended;
    /// <summary>Queued until the API executes the action; terminal results are retained.</summary>
    public StoryActionStatus Status => _status == StoryActionStatus.Queued ? _ended?.Invoke() ?? _status : _status;
    public string Detail { get; private set; }
    internal StoryActionResult(StoryActionStatus status, string detail = "", Func<StoryActionStatus?>? ended = null)
    { _status = status; Detail = detail; _ended = ended; }
    internal void Finish(StoryActionStatus status, string detail) { _status = status; Detail = detail; }
}

/// <summary>An authored mission definition. Register once; event arguments identify live occurrences and their games.</summary>
public interface IStoryDefinition : IDisposable
{
    StoryContentId Id { get; }
    event Action<IStoryMission>? Accepted;
    event Action<IStoryMission>? Completed;
    event Action<IStoryMission>? Failed;
    event Action<IStoryMission>? Abandoned;
    event Action<IStoryMission>? Changed;
}

public interface IStory
{
    IGame Game { get; }
    /// <summary>Offer an occurrence in this game. The mission owns safe execution and reports its result through Changed/LastAction.</summary>
    IStoryMission Offer(IStoryDefinition definition);
    StoryMissionQuery GetMissions(IStoryDefinition definition);
}

public sealed class StoryMissionQuery
{
    public bool IsAvailable { get; }
    public IReadOnlyList<IStoryMission> Missions { get; }
    public string Detail { get; }
    internal StoryMissionQuery(bool available, IReadOnlyList<IStoryMission> missions, string detail = "")
    { IsAvailable = available; Missions = missions; Detail = detail; }
}

public interface IStoryMission
{
    IGame Game { get; }
    IStoryDefinition Definition { get; }
    Guid Id { get; }
    StoryMissionState State { get; }
    IReadOnlyDictionary<string, string> Choices { get; }
    StoryActionResult LastAction { get; }
    event Action<IStoryMission>? Changed;
    StoryActionResult Activate();
    StoryActionResult Withdraw();
    StoryActionResult Fail(IReadOnlyDictionary<string, string>? choices = null);
    StoryActionResult Abandon(IReadOnlyDictionary<string, string>? choices = null);
    StoryActionResult DeclareChoices(IReadOnlyDictionary<string, string> choices);
    IStoryObjective GetObjective(string key);
}

public interface IStoryObjective
{
    IStoryMission Mission { get; }
    string Key { get; }
    StoryObjectiveQuery Snapshot { get; }
    /// <summary>Observed progress changes, including native trigger-driven progression. Fires as a safe gameplay reaction.</summary>
    event Action<IStoryObjective>? Changed;
    /// <summary>Set absolute scripted progress. Native objective types remain game-owned.</summary>
    StoryActionResult SetProgress(int progress);
}

using System;

namespace VGModAPI;

public enum BarPatronStatus { Waiting, Assigned, Removed, Unavailable, GameEnded }

public sealed class BarRegistrationResult
{
    public BarStatus Status { get; }
    public string Detail { get; }
    public bool Succeeded => Status == BarStatus.Succeeded;
    public IBarPatronDefinition? Definition { get; }
    internal BarRegistrationResult(BarStatus status, IBarPatronDefinition? definition = null, string detail = "")
    { Status = status; Definition = definition; Detail = detail; }
}

/// <summary>A keyed placement declaration. The API places it in each game and reconstructs its saved presentation.</summary>
public interface IBarPatronDefinition : IDisposable
{
    BarPatronId Id { get; }
    event Action<IBarPatron>? Interacted;
}

public interface IBars
{
    IGame Game { get; }
    IBarPatron Get(IBarPatronDefinition definition);
}

public interface IBarPatron
{
    IGame Game { get; }
    IBarPatronDefinition Definition { get; }
    BarPatronStatus Status { get; }
    BarResult LastAction { get; }
    event Action<IBarPatron>? Changed;
    /// <summary>Hide this patron in this game. Absence survives reload until Restore is requested.</summary>
    BarResult Remove();
    BarResult Restore();
}

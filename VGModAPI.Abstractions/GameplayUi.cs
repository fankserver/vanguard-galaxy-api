using System;

namespace VGModAPI;

/// <summary>One observed gameplay UI lifetime. Neither identity is persistent save data.</summary>
public sealed class GameplayUiSnapshot
{
    public Guid Id { get; }
    public Guid SessionId { get; }
    internal GameplayUiSnapshot(Guid sessionId) { Id = Guid.NewGuid(); SessionId = sessionId; }
}

/// <summary>Replacement delivers (previous, null) before (null, current). Events do not replay.</summary>
public sealed class GameplayUiChange
{
    public GameplayUiSnapshot? Previous { get; }
    public GameplayUiSnapshot? Current { get; }
    internal GameplayUiChange(GameplayUiSnapshot? previous, GameplayUiSnapshot? current)
    { Previous = previous; Current = current; }
}

/// <summary>
/// Observed gameplay UI existence, not visibility or universal world readiness. Main-thread only.
/// Subscribe to Changed, then read Current. Container access is provided by VGModAPI.Unity.
/// </summary>
public interface IGameplayUiService : IServiceStatus
{
    GameplayUiSnapshot? Current { get; }
    event Action<GameplayUiChange>? Changed;
}

public enum GameplayUiContainerStatus
{
    Created,
    Unavailable,
    NoHost,
    StaleHost,
    DuplicateIdentity,
    LimitReached,
    CreationFailed
}

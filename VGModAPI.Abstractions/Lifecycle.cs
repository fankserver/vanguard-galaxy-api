using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum SessionPhase { None, Starting, PlayerReady, GameplayInitialized, Failed, Invalidated }
public enum SessionOrigin { SaveLoad, NewGame }
public enum LifecycleEventKind { SessionStarting, SessionInvalidated, PlayerReady, GameplayInitialized, SessionStartFailed, SaveStarted, SaveSucceeded, SaveSkipped, SaveFailed }

/// <summary>A runtime attempt, not a campaign or save identity. No vanilla object references are exposed.</summary>
public sealed class SessionSnapshot
{
    public Guid Id { get; }
    public SessionPhase Phase { get; }
    public SessionOrigin Origin { get; }
    public string? SavePath { get; }

    public SessionSnapshot(Guid id, SessionPhase phase, SessionOrigin origin, string? savePath)
    { Id = id; Phase = phase; Origin = origin; SavePath = savePath; }
}

public sealed class LifecycleEvent
{
    public LifecycleEventKind Kind { get; }
    public SessionSnapshot? Session { get; }
    public Guid? OperationId { get; }
    public string? Destination { get; }
    public string? Detail { get; }

    public LifecycleEvent(LifecycleEventKind kind, SessionSnapshot? session, Guid? operationId = null, string? destination = null, string? detail = null)
    { Kind = kind; Session = session; OperationId = operationId; Destination = destination; Detail = detail; }
}

public sealed class CapabilityStatus
{
    public string Name { get; }
    public bool Available { get; }
    public bool RuntimeQualified { get; }
    public string Detail { get; }
    public CapabilityStatus(string name, bool available, bool runtimeQualified, string detail)
    { Name = name; Available = available; RuntimeQualified = runtimeQualified; Detail = detail; }
}

/// <summary>All members and subscription disposal are Unity-main-thread-only. Registration does not replay events.</summary>
public interface ILifecycleApi
{
    SessionSnapshot? CurrentSession { get; }
    IReadOnlyList<CapabilityStatus> Capabilities { get; }
    IDisposable Subscribe(string owner, Action<LifecycleEvent> callback);
}

/// <summary>Optional since 0.1.1. Main-thread-only; false is not a readiness or mutation guarantee.</summary>
public interface ILifecycleDispatchState
{
    /// <summary>True throughout callback delivery, including queued reentrant events and error reporting.</summary>
    bool IsDispatchingCallbacks { get; }
}

/// <summary>Available after the API plugin's Awake; declare a hard BepInEx dependency on vgmodapi.</summary>
public static class ModApi
{
    public const string PluginId = "vgmodapi";
    public static ILifecycleApi? Current { get; internal set; }
    /// <summary>Process-scoped loader inventory, independent of game-hook availability. Refresh on the main thread.</summary>
    public static IModInformationCatalog? Mods { get; internal set; }
    /// <summary>Null unless experimental persistence is explicitly enabled and initialized.</summary>
    public static IPersistenceApi? Persistence { get; internal set; }
    /// <summary>Optional mission observer. Availability does not establish persistent instance continuity.</summary>
    public static IMissionEvents? Missions { get; internal set; }
    /// <summary>Optional native travel observer; non-null only when the travel group is bound and enabled.</summary>
    public static ITravelEvents? Travel { get; internal set; }
    /// <summary>Optional experimental recipe definitions for the current station; null when disabled/unavailable.</summary>
    public static IRecipeCatalog? Recipes { get; internal set; }
    /// <summary>Optional main-thread station requirements and output estimates; advisory, not reservations.</summary>
    public static IRecipeQuotes? RecipeQuotes { get; internal set; }
    public static ICraftingJobs? CraftingJobs { get; internal set; }
    /// <summary>Optional inspected-build boarding observations; consult boarding-observation capability.</summary>
    public static IBoardingEvents? Boarding { get; internal set; }
    /// <summary>Optional inspected-build boarding policies, independent of observation subscribers.</summary>
    public static IBoardingRules? BoardingRules { get; internal set; }
    /// <summary>Optional inspected-build boarding commands; admitted commands are not completed outcomes.</summary>
    public static IBoardingCommands? BoardingCommands { get; internal set; }
    public static IBoardingTactics? BoardingTactics { get; internal set; }
    public static IBoardingCombatRules? BoardingCombat { get; internal set; }
    /// <summary>Optional experimental authored dungeon content with API-owned save data; requires API 0.1.30.</summary>
    public static IDungeonContent? Dungeons { get; internal set; }
    public static IDungeonRewardRules? DungeonRewards { get; internal set; }
    public static IDungeonSettlement? DungeonSettlement { get; internal set; }
    /// <summary>Optional station-lifetime observer; non-null only when the travel group is bound and enabled.</summary>
    public static IStationEvents? Station { get; internal set; }
    /// <summary>
    /// Optional owned-story surface (since 0.1.12); non-null only when the story group is bound and
    /// enabled, which requires the inspected assembly, API-managed saves and the native story
    /// catalog. Acquire a provider lease from your plugin's own Awake, before any session begins.
    /// </summary>
    public static IStoryApi? Story { get; internal set; }

    /// <summary>Optional experimental owned station-bar content. Null when unavailable.</summary>
    public static IBarApi? Bars { get; internal set; }
}

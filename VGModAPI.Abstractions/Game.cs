using System;

namespace VGModAPI;

/// <summary>A captured running game. It never rebinds to a different save or a replacement load.</summary>
public interface IGame
{
    /// <summary>False once this game has ended. Operations still check lifetime themselves.</summary>
    bool IsActive { get; }
    INavigation Navigation { get; }
    IInventories Inventories { get; }
    IStory Story { get; }
    IBars Bars { get; }
}

/// <summary>Stable plugin-lifetime access to running games. Subscribe once during plugin setup.</summary>
public interface IGameService
{
    IGame? Current { get; }
    /// <summary>Delivered when a game can accept gameplay reactions; no replay. The argument owns
    /// the event's game, so follow-up operations need no session tokens or current-game lookup.</summary>
    event Action<IGame>? Started;
}

/// <summary>Navigation within one captured game; an old game cannot focus a replacement game's map.</summary>
public interface INavigation
{
    NavigationStationsResult GetStations(bool visitedOnly = true);
    JumpCountsResult GetJumpCounts(string fromSystemId);
    JumpCountResult GetJumpCount(string fromSystemId, string toSystemId);
    NavigationStatus FocusPoi(string poiId);
    NavigationStatus FocusWorldSite(WorldSiteReference reference);
}

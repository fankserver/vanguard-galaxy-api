namespace VGModAPI;

/// <summary>
/// The bounded region the game lays settled subsectors out in — its own "frontier" placement band. Authored
/// content placed inside these bounds lands on the belt/galaxy map and stays within its zoom range; anything
/// outside is off-map (the galaxy map cannot pan or zoom far enough to show it). Exposed so consumers can
/// reason about placement instead of hard-coding the numbers.
/// </summary>
public static class SettledMapBounds
{
    /// <summary>Left edge of the settled band on the subsector grid.</summary>
    public const float MinX = -38f;
    /// <summary>Right edge of the settled band on the subsector grid.</summary>
    public const float MaxX = 38f;
    /// <summary>Bottom edge of the settled band on the subsector grid.</summary>
    public const float MinY = -6f;
    /// <summary>Top edge of the settled band on the subsector grid.</summary>
    public const float MaxY = 6f;
    /// <summary>The minimum clearance the game keeps between a new subsector and every existing one.</summary>
    public const float MinSeparation = 6f;

    /// <summary>Whether a subsector grid position is inside the settled band (and therefore on the map).</summary>
    public static bool Contains(float x, float y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
}

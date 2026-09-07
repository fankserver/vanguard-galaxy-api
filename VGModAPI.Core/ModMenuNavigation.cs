using System;

namespace VGModAPI.Core;

internal static class ModMenuNavigation
{
    internal static (int? Up, int? Down, int Left, int Right) Neighbors(int index, int count, bool verticalScrollbar)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
        if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        var previous = (index + count - 1) % count;
        var next = (index + 1) % count;
        // UGUI consumes vertical movement as scrolling only when these neighbors are absent.
        return (verticalScrollbar ? (int?)null : previous, verticalScrollbar ? (int?)null : next, previous, next);
    }
}

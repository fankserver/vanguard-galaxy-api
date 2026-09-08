using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace VGModAPI.Qualification;

internal static class ForgeReadRegistry
{
    internal static object[] Expand(IEnumerable<object> parents, Func<object, IEnumerable<object>> children)
    {
        var seen = new HashSet<object>(Identity.Instance);
        var result = new List<object>();
        foreach (var parent in parents)
        {
            if (seen.Add(parent)) result.Add(parent);
            foreach (var child in children(parent))
                if (seen.Add(child)) result.Add(child);
        }
        return result.ToArray();
    }
    private sealed class Identity : IEqualityComparer<object>
    {
        internal static readonly Identity Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}

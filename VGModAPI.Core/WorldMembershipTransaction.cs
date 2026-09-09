using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace VGModAPI.Core;

/// <summary>Exact-membership append to a concrete native list; never relocates or replaces existing entries.</summary>
internal static class WorldMembershipTransaction
{
    internal const int MaxMembers = 10000;

    internal static object[] Capture(IList members)
    {
        RequireConcreteList(members);
        if (members.Count > MaxMembers) throw new InvalidDataException("World membership exceeds snapshot bound.");
        var result = new object[members.Count];
        for (int i = 0; i < result.Length; i++) result[i] = members[i] ?? throw new InvalidDataException("Null world member.");
        return result;
    }

    internal static bool TryAppend(IList members, object[] expected, object created, Func<bool> isCurrent)
    {
        RequireConcreteList(members);
        if (expected == null || created == null || isCurrent == null) throw new ArgumentNullException("World append inputs required.");
        if (expected.Length >= MaxMembers) return false;
        // Freeze before the caller's lifecycle/ownership fence; it cannot rewrite the expectation.
        var snapshot = (object[])expected.Clone();
        var element = members.GetType().GetGenericArguments()[0];
        if (!element.IsInstanceOfType(created)) throw new ArgumentException("Incorrect native member type.", nameof(created));
        foreach (var item in snapshot)
            if (item == null || ReferenceEquals(item, created)) return false;
        if (!Matches(members, snapshot) || !isCurrent() || !Matches(members, snapshot)) return false;
        // List<T>.Add has no provider callback or virtual collection mutation. All fallible validation
        // precedes the append; this helper does not commit a separate registry or persistence record.
        members.Add(created);
        return true;
    }

    private static bool Matches(IList members, object[] snapshot)
    {
        if (members.Count != snapshot.Length) return false;
        for (int i = 0; i < snapshot.Length; i++) if (!ReferenceEquals(members[i], snapshot[i])) return false;
        return true;
    }

    private static void RequireConcreteList(IList members)
    {
        if (members == null) throw new ArgumentNullException(nameof(members));
        var type = members.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(List<>))
            throw new ArgumentException("A concrete native List<T> is required.", nameof(members));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Qualification;

internal static class StoryReceipt
{
    internal const string Phase = "owned-story-v1";
    internal static readonly string[] RequiredCases =
    {
        "independent-authors", "offered-roundtrip", "active-roundtrip", "native-completion",
        "save-refusals", "older-save-rollback", "cross-slot-return", "repeat-job",
        "provider-unregistered-first-reload", "provider-unregistered-second-reload"
    };

    // Each case is recorded only after its native assertions. This phase does not claim absent
    // assemblies, new-game content or schema migration; those need their own named receipts.
    internal static string? Evaluate(IReadOnlyList<string> cases)
    {
        if (cases.Count != RequiredCases.Length) return "Missing or duplicate story cases.";
        if (!cases.SequenceEqual(RequiredCases, StringComparer.Ordinal)) return "Unexpected story case order or identity.";
        return null;
    }
}

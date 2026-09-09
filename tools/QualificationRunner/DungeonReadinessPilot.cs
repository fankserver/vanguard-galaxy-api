using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // This phase proves initialization/read admission only, not gameplay or persistence acceptance.
    private IEnumerable<object?> CheckDungeonReadiness()
    {
        WriteAtomic("dungeon-readiness.txt", new[] { "INCOMPLETE" });
        foreach (var frame in LoadReady("fixture-a")) yield return frame;
        foreach (var frame in Wait(NativeTravelReady, "Dungeon fixture readiness")) yield return frame;
        foreach (var capability in new[] { "boarding-observation", "boarding-rules", "boarding-commands", "boarding-tactics", "boarding-combat", "dungeon-content", "dungeon-rewards", "dungeon-panel-opening", "dungeon-panel-actions", "dungeon-panel-sections" })
            Require(_api!.Capabilities.Any(value => value.Name == capability && value.Available), "Unavailable dungeon capability: " + capability);
        var boarding = ModApi.Boarding ?? throw new InvalidOperationException("Boarding observation unavailable.");
        Require(ModApi.BoardingRules != null && ModApi.BoardingCommands != null && ModApi.BoardingTactics != null && ModApi.BoardingCombat != null,
            "Boarding service publication incomplete.");
        Require(ModApi.Dungeons != null && ModApi.DungeonRewards != null && ModApi.DungeonSettlement != null && ModApi.DungeonPanel != null, "Dungeon service publication incomplete.");
        var session = _api!.CurrentSession!.Id;
        Require(boarding.SessionId == session, "Boarding session differs from lifecycle.");
        var targets = boarding.GetTargets(); var operations = boarding.GetOperations();
        Require(operations.Count == 0, "Readiness fixture must not contain an active boarding operation; use a recovery-specific scenario.");
        Require(targets.All(target => target.Handle.SessionId == session), "Cross-session target observed.");
        Require(operations.All(operation => operation.Handle.SessionId == session && operation.Target.SessionId == session), "Cross-session operation observed.");
        // Do not open a panel here: native opening can resume an operation or recover extraction.
        WriteAtomic("dungeon-readiness.txt", new[] { "PASS", "dungeon-readiness-v1", "targets=" + targets.Count, "operations=" + operations.Count });
        using (var sha = System.Security.Cryptography.SHA256.Create())
            WriteAtomic("dungeon-readiness.receipt", new[] { "PASS", "dungeon-readiness-v1", "sha256=" + BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(_root!, "dungeon-readiness.txt")))).Replace("-", "").ToLowerInvariant() });
        Passed("Dungeon initialization and observed-session consistency only");
    }
}

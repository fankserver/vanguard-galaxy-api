using System;
using System.Linq;
using VGModAPI;

namespace EWTest.Suites;

/// <summary>Collates the lifecycle events observed during the run and records which session phases
/// were actually reached, so a game update that stops emitting lifecycle events is caught.</summary>
public sealed class LifecycleSuite
{
    private readonly Plugin _plugin;
    internal readonly SuiteResult Sink = new() { Id = "lifecycle", Name = "Lifecycle session observation" };

    public LifecycleSuite(Plugin plugin) => _plugin = plugin;

    public SuiteResult Finish()
    {
        var events = _plugin.LifecycleEvents;
        bool sawStarting = events.Any(e => e.Kind == LifecycleEventKind.SessionStarting);
        bool sawReady = events.Any(e => e.Kind == LifecycleEventKind.PlayerReady);
        bool sawGameplay = events.Any(e => e.Kind == LifecycleEventKind.GameplayInitialized);

        // Session-phase ordering is the strongest signal that the load/new-game path still works
        // end to end on an updated build.
        if (sawGameplay)
            Sink.Results.Add(Check.Pass("session reached GameplayInitialized", "observed"));
        else if (Sink.Results.Count == 0)
            Sink.Results.Add(Check.Fail("session reached GameplayInitialized", "not observed",
                "GameplayInitialized within timeout",
                "Wire EWTest session-entry automation (launching into / auto-continuing a " +
                "disposable save) so the live gameplay suites can run; see docs/development/e2e-tests.md."));

        Sink.Results.Add(Check.Run("SessionStarting event fired", "true", () =>
        {
            if (!sawStarting)
                throw new InvalidOperationException("no SessionStarting event (ready=" + sawReady +
                    ", gameplay=" + sawGameplay + ")");
        }, "Re-inspect LifecyclePatches session hooks and GameAdapter loading in the updated Assembly-CSharp.dll."));

        return Sink;
    }
}

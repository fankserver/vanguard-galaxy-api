using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;
using EWTest.Suites;

namespace EWTest;

/// <summary>
/// Builds the ordered list of frame steps that run on the Unity main thread.
/// Each step is a predicate evaluated every frame: a step returns true when
/// complete (allowing the next step to start), and false when it must wait
/// (e.g. while a gameplay session is pending). Bounded frame timeouts turn a
/// never-satisfied wait into a targeted failure rather than an endless hang,
/// so `make e2e` always terminates with a report.
/// </summary>
public sealed class EWRunner
{
    private readonly Plugin _plugin;
    private readonly List<string> _names = new();

    public EWRunner(Plugin plugin) => _plugin = plugin;

    /// <summary>Returns a one-frame step that runs and streams the finished suite.</summary>
    private Func<bool> RunSuite(string name, Func<SuiteResult> produce)
    {
        _names.Add(name);
        return () =>
        {
            _plugin.StreamSuite(produce());
            return true;
        };
    }

    /// <summary>Returns a waiting step: completes when <paramref name="condition"/> is true or it
    /// has waited <paramref name="maxFrames"/> frames. On timeout it records a failure into
    /// <paramref name="sink"/> with the described action.</summary>
    private Func<bool> WaitUntil(string name, Func<bool> condition, int maxFrames,
                                 SuiteResult sink, string onTimeout)
    {
        _names.Add(name);
        int waited = 0;
        return () =>
        {
            if (condition()) return true;
            if (++waited >= maxFrames)
            {
                sink.Results.Add(Check.Fail(name, "timeout after ~" + (waited / 60) + "s",
                    "condition within " + maxFrames + " frames", onTimeout));
                return true;
            }
            return false;
        };
    }

    public IEnumerable<Func<bool>> Build()
    {
        // The availability suite needs no session and runs at the menu. It is the fastest,
        // highest-signal detector of a game-update hook-binding break.
        var freshOnly = Environment.GetEnvironmentVariable("EWTEST_SUITE") == "fresh-session";
        if (!freshOnly)
            yield return RunSuite("availability", () => new AvailabilitySuite(_plugin).Run());

        // Enter fresh ephemeral gameplay through the native new-player boundary.
        var lifecycleSuite = new LifecycleSuite(_plugin);
        yield return WaitUntil("native fresh session entry", FreshSession.TryStart,
            Plugin.MaxSessionWaitFrames, lifecycleSuite.Sink,
            "Re-inspect MainMenuUI.instance and StartTestArena in Assembly-CSharp.dll.");
        yield return WaitUntil(
            "session reached (GameplayInitialized)",
            () => _plugin.SessionReached,
            Plugin.MaxSessionWaitFrames,
            lifecycleSuite.Sink,
            "Inspect native new-player setup and ModAPI lifecycle bindings; gameplay was not observed.");
        yield return RunSuite("fresh-session", () =>
        {
            var result = new SuiteResult { Id = "fresh-session", Name = "Native ephemeral gameplay session" };
            result.Results.Add(Check.Run("native Test Arena reaches API gameplay session", "ephemeral player and GameplayInitialized", () =>
            {
                if (!FreshSession.IsEphemeral() || !FreshSession.GameplayInitialized() || !_plugin.SessionReached)
                    throw new InvalidOperationException("Native ephemeral gameplay or API gameplay event missing");
                var observed = _plugin.LifecycleEvents.Select(e => e.Kind).ToArray();
                var starting = Array.IndexOf(observed, LifecycleEventKind.SessionStarting);
                var ready = Array.IndexOf(observed, LifecycleEventKind.PlayerReady);
                var initialized = Array.IndexOf(observed, LifecycleEventKind.GameplayInitialized);
                if (starting < 0 || ready <= starting || initialized <= ready)
                    throw new InvalidOperationException("API lifecycle events missing or out of order: " + string.Join(", ", observed));
            }, "Inspect GamePlayer.CreateTestArenaPlayer, GameManager.StartNewGame and lifecycle bindings."));
            return result;
        });
        if (freshOnly) yield break;
        yield return RunSuite("lifecycle", () => lifecycleSuite.Finish());

        yield return RunSuite("world-authoring", () => new WorldAuthoringSuite(_plugin).Run());
        yield return RunSuite("dungeon", () => new DungeonSuite(_plugin).Run());
    }

    public string Describe() => string.Join(", ", _names);
}

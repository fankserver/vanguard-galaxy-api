using System;
using VGModAPI;

namespace EWTest.Suites;

/// <summary>
/// Live dungeon-authoring surface check: acquire the provider, register a minimal valid authored
/// dungeon definition, and confirm registration is accepted; then dispose the provider. Attachment
/// to a live boarding target and the choice/combat flows are asynchronous gameplay steps that are
/// documented follow-ups (they need an on-game-machine session to iterate reliably).
/// </summary>
public sealed class DungeonSuite
{
    private readonly Plugin _plugin;
    public DungeonSuite(Plugin plugin) => _plugin = plugin;

    private const string ProviderId = "ewtest";
    private const string LocalId = "ewtest-dungeon";

    public SuiteResult Run()
    {
        var suite = new SuiteResult { Id = "dungeon", Name = "Live dungeon authoring" };

        if (!FreshSession.IsEphemeral() || !_plugin.SessionReached)
        {
            suite.Results.Add(Check.Fail("isolated session required", "not verified", "ephemeral gameplay session",
                "Fix native fresh-session entry before dungeon mutations."));
            return suite;
        }
        IDungeonProvider? provider = null;
        try
        {
            provider = ModApi.Services.Dungeons.AcquireProvider(ProviderId);
            if (provider == null)
            {
                suite.Results.Add(Check.Skip("dungeon provider", "dungeon service unavailable in this session"));
                return suite;
            }

            // Minimal valid layout: one unlocked airlock + one corridor, symmetric, connected.
            var airlock = new DungeonCompartmentDefinition("airlock", CompartmentType.Airlock, new[] { "corridor" });
            var corridor = new DungeonCompartmentDefinition("corridor", CompartmentType.Corridor, new[] { "airlock" });
            var layout = new DungeonLayout(new[] { airlock, corridor });
            var choice = new DungeonChoiceDefinition("proceed", "Proceed");
            var ev = new DungeonEventDefinition("reveal", "corridor", "Corridor", new[] { choice });
            var definition = new DungeonDefinition(1, "E2E Dungeon", layout, events: new[] { ev });

            suite.Results.Add(Check.Run("register authored dungeon", "non-null registration", () =>
            {
                using var registration = provider.Register(LocalId, definition, allowChoice: null);
                if (registration == null) throw new InvalidOperationException("Register returned null");
            }, "Re-inspect dungeon definition validation + DungeonLayout/registry in the updated build."));

            suite.Results.Add(Check.Run("dungeon snapshots readable", "no exception", () =>
            {
                _ = provider.GetDungeons();
            }, "Re-inspect DungeonStateStore snapshot reading."));
        }
        catch (Exception ex)
        {
            suite.Results.Add(Check.Fail("dungeon suite", ex.GetType().Name + ": " + ex.Message,
                "no exception", "Unexpected live failure; inspect the updated build's dungeon integration."));
        }
        finally
        {
            try { provider?.Dispose(); } catch { }
        }
        return suite;
    }
}

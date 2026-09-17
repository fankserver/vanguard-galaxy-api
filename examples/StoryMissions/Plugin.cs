using System;
using BepInEx;
using VGModAPI;

namespace StoryMissions;

/// <summary>
/// Sample/test mod demonstrating owned story authoring end to end: a hand-authored **campaign** beat
/// with a decision the player makes, a **generated job** built from runtime text, and a follow-up
/// mission offered automatically when the campaign completes.
///
/// Everything is driven from one HUD panel so the whole lifecycle is reachable in game:
///
///   register (Start) -> Offer -> Activate -> SetProgress -> DeclareChoices -> Completed -> follow-up
///
/// The API owns persistence. This plugin has no save hook, serializer or load callback: definitions
/// are registered once, and the API restores missions, progress and declared choices itself.
/// </summary>
[BepInPlugin(Id, "Story Missions example", "1.0.0")]
    // Version floor omitted on purpose: in-repo examples track the development API, which
    // is deliberately unversioned (0.0.0); real consumers name the release they require.
    [BepInDependency(ModApi.PluginId)]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.story-missions";

    // Local definition identities (author-local; the API owns the provider-scoped content id).
    private const string CampaignDef = "witness-account";
    private const string FollowUpDef = "witness-followup";
    private const string JobDef = "field-report";

    // Objective keys the example advances by script.
    private const string TalkKey = "talk";
    private const string ReportKey = "report";

    private const string Faction = "Fanatics";

    private IStoryProvider? _provider;
    private IGameService? _games;
    private IHudRegistration? _hud;

    private IStoryDefinition? _campaign;
    private IStoryDefinition? _followUp;
    private IStoryDefinition? _job;
    private IStoryMission? _activeCampaign;
    private IStoryMission? _activeJob;
    private string _lastAction = "registered";
    private int _followUpsOffered;

    private void Awake()
    {
        // Registration is deferred to Start(): BepInEx only populates PluginInfos[].Instance after
        // Awake returns, so the story provider's host authentication cannot succeed here.
    }

    private void Start()
    {
        var acquired = ModApi.Services.Story.AcquireProvider(this);
        _provider = acquired.Provider;
        if (_provider == null) { Logger.LogWarning("Story authoring unavailable: " + acquired.Diagnostic); return; }

        _campaign = Register(CampaignDefinition(), "campaign");
        _followUp = Register(FollowUpDefinition(), "follow-up");
        _job = Register(JobDefinition("A courier wants three field reports filed before the next burn."), "job");

        // A completed campaign offers its follow-up in the SAME game that produced the event. The API
        // delivers this as a safe gameplay reaction, so ordinary follow-up actions are allowed here.
        if (_campaign != null) _campaign.Completed += OnCampaignCompleted;

        _games = ModApi.Services.Game;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    private IStoryDefinition? Register(StoryMissionDefinition definition, string label)
    {
        var result = _provider!.Register(definition);
        if (result.Definition == null) Logger.LogWarning($"Story {label} registration refused: {result.Status} - {result.Diagnostic}");
        return result.Definition;
    }

    /// <summary>A retained campaign beat: listen, then choose a reply. The reply is a declared choice.</summary>
    private static StoryMissionDefinition CampaignDefinition() => new StoryMissionDefinition(
        CampaignDef, "A witness's account", "Listen to the witness, then choose your reply.",
        new StoryFactionId(Faction),
        new[]
        {
            new StoryStep("Listen to the witness", new[] { StoryObjective.Scripted(TalkKey, "Hear the witness out", 3) }),
            new StoryStep("Choose an answer", new[] { StoryObjective.Scripted(ReportKey, "Promise to investigate") }),
        },
        new[] { StoryReward.Credits(17), StoryReward.Experience(5) },
        completionText: "The witness will remember what you promised.",
        choiceKeys: new[] { "witness" });

    /// <summary>Offered automatically once the campaign completes; returns the player to the source.</summary>
    private static StoryMissionDefinition FollowUpDefinition() => new(
        FollowUpDef, "What the witness left out", "Go back and press the contact for the rest of it.",
        new StoryFactionId(Faction),
        new[] { new StoryStep("Return to the contact", new[] { StoryObjective.ReturnToSource() }) },
        new[] { StoryReward.Credits(23) });

    /// <summary>
    /// A generated job: the pitch text is supplied at runtime, but it uses exactly the same
    /// owner-scoped API as hand-authored content. Like every story mission it persists as one
    /// permanent entry; the API keeps its outcome.
    /// </summary>
    private static StoryMissionDefinition JobDefinition(string generatedPitch) => new(
        JobDef, "Generated field report", generatedPitch,
        new StoryFactionId(Faction),
        new[]
        {
            new StoryStep("Collect reports", new[] { StoryObjective.Scripted("file", generatedPitch, 3) }),
            new StoryStep("Bank the fee", new[] { StoryObjective.CollectCredits(500).WithKey("fee") }),
        },
        new[] { StoryReward.Credits(3) });

    private void OnCampaignCompleted(IStoryMission mission)
    {
        if (_followUp == null) return;
        // mission.Game is the game that produced the event: no current-game lookup, no session token.
        var offered = mission.Game.Story.Offer(_followUp);
        _followUpsOffered++;
        _lastAction = "follow-up offered (" + offered.State + ")";
        Logger.LogInfo("Campaign completed; follow-up offered automatically.");
        RefreshPanel();
    }

    private void RefreshPanel()
    {
        if (_hud == null) return;
        _hud.Update(null, new HudPanel("Story Missions",
            new[]
            {
                new HudRow("offer-campaign", _activeCampaign == null ? "Offer campaign" : "Campaign: " + _activeCampaign.State,
                    "offer and activate the hand-authored campaign beat",
                    "Offers 'A witness's account' in the current game and activates it. Story outcomes and "
                    + "declared choices are retained by the API; a definition admits one mission.",
                    clickable: _activeCampaign == null),
                new HudRow("talk", "Hear the witness (+1)",
                    "advance the scripted 'talk' objective by one",
                    "Scripted objectives are advanced with absolute progress via SetProgress. Native objective "
                    + "types stay game-owned; this one is authored, so the mod drives it.",
                    clickable: _activeCampaign != null),
                new HudRow("answer", "Answer the witness",
                    "complete the reply objective and declare the 'witness' choice",
                    "Completes the second step and records a declared choice. The API saves the choice; this "
                    + "plugin writes no save data of its own.",
                    clickable: _activeCampaign != null),
                new HudRow("offer-job", _activeJob == null ? "Offer generated job" : "Job: " + _activeJob.State,
                    "offer the runtime-generated temporary job",
                    "Generated text, same owner-scoped API; its outcome is retained like any story mission.",
                    clickable: _activeJob == null),
                new HudRow("abandon", "Abandon active missions",
                    "withdraw whatever is active",
                    "Abandon reports a typed result rather than silently dropping the mission.",
                    clickable: _activeCampaign != null || _activeJob != null),
                new HudRow("status", StatusLine(), "Live mission state; follow-ups offered so far: " + _followUpsOffered),
            },
            closable: false));
    }

    private string StatusLine()
    {
        string S(IStoryMission? m) => m == null ? "-" : m.State.ToString();
        return $"campaign:{S(_activeCampaign)} job:{S(_activeJob)} | last: {_lastAction}";
    }

    private void OnHud(HudInteraction interaction)
    {
        if (interaction.Kind != HudInteractionKind.Row) return;
        try
        {
            switch (interaction.RowId)
            {
                case "offer-campaign": _activeCampaign = OfferAndActivate(_campaign); break;
                case "talk": Advance(TalkKey); break;
                case "answer": Answer(); break;
                case "offer-job": _activeJob = OfferAndActivate(_job); break;
                case "abandon": AbandonAll(); break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    private IStoryMission? OfferAndActivate(IStoryDefinition? definition)
    {
        var game = _games?.Current;
        if (definition == null || game == null) { _lastAction = "no game / definition"; return null; }
        var mission = game.Story.Offer(definition);
        var activated = mission.Activate();
        _lastAction = "offer+activate: " + activated.Status + (activated.Detail.Length > 0 ? " (" + activated.Detail + ")" : "");
        return mission;
    }

    /// <summary>
    /// Absolute scripted progress: read the current amount, then set the next one. Progress is
    /// nullable because the API reports what it actually knows — an unknown amount is not treated
    /// as zero.
    /// </summary>
    private void Advance(string key)
    {
        if (_activeCampaign == null) return;
        var objective = _activeCampaign.GetObjective(key);
        var snapshot = objective.Snapshot;
        if (snapshot.Progress is not int current)
        { _lastAction = $"{key}: progress unknown ({snapshot.Knowledge})"; return; }
        var next = current + 1;
        var result = objective.SetProgress(next);
        _lastAction = $"{key} -> {next}/{snapshot.Required?.ToString() ?? "?"}: {result.Status}";
    }

    private void Answer()
    {
        if (_activeCampaign == null) return;
        var choice = _activeCampaign.DeclareChoices(new System.Collections.Generic.Dictionary<string, string> { ["witness"] = "promised" });
        var progress = _activeCampaign.GetObjective(ReportKey).SetProgress(1);
        _lastAction = $"answer: choice: {choice.Status}; progress: {progress.Status}";
    }

    private void AbandonAll()
    {
        if (_activeCampaign != null) { _lastAction = "campaign abandon: " + _activeCampaign.Abandon().Status; _activeCampaign = null; }
        if (_activeJob != null) { _lastAction += "; job abandon: " + _activeJob.Abandon().Status; _activeJob = null; }
    }

    private void OnDestroy()
    {
        if (_campaign != null) _campaign.Completed -= OnCampaignCompleted;
        var hud = _hud; _hud = null; hud?.Dispose();
        // Disposing the provider releases its registrations; persisted content stays with the save.
        _provider?.Dispose(); _provider = null;
        _campaign = null; _followUp = null; _job = null; _games = null;
    }
}

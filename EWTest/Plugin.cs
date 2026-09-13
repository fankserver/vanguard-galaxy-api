using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;
using VGModAPI;

namespace EWTest;

/// <summary>
/// Development-only in-game E2E harness. Opt-in: when launched by `make e2e` it receives
/// EWTEST_RUN=1 and an EWTEST_REPORT_PATH environment variable, then auto-runs a suite of
/// live assertions against the running game, writes a machine-readable report, and quits.
/// When left unset it stays idle, so the harness is harmless even if copied into a normal
/// install (it is never shipped in the release package regardless).
/// </summary>
[BepInPlugin(Id, "VGModAPI E2E harness", "1.0.0")]
[BepInDependency(ModApi.PluginId)]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "vgmodapi.devtools.e2e";
    private const string ReportPathEnv = "EWTEST_REPORT_PATH";
    private const string RunEnv = "EWTEST_RUN";
    internal const int MaxSessionWaitFrames = 18_000; // ~5 min at 60fps
    internal const int MaxReconstructionWaitFrames = 9_000; // ~2.5 min

    private readonly E2EReport _report = new();
    private readonly List<Func<bool>> _steps = new();
    private readonly List<LifecycleEvent> _lifecycleEvents = new();

    private ILifecycleService? _lifecycle;
    private string? _reportPath;
    private bool _started;
    private bool _finalized;
    private bool _sessionReached;

    private void Awake()
    {
        _reportPath = Environment.GetEnvironmentVariable(ReportPathEnv);
        if (Environment.GetEnvironmentVariable(RunEnv) != "1" || string.IsNullOrEmpty(_reportPath))
        {
            Logger.LogInfo("EWTest idle (EWTEST_RUN != 1 or no EWTEST_REPORT_PATH).");
            return;
        }
        _started = true;

        PopulateMeta();
        try
        {
            // Services are published on the API plugin's Awake; this runs on the main thread too.
            _lifecycle = ModApi.Services.Lifecycle;
            _lifecycle.Changed += OnLifecycle;
        }
        catch (Exception ex)
        {
            Logger.LogError("EWTest could not subscribe to lifecycle: " + ex);
        }

        var runner = new EWRunner(this);
        runner.Build().ForEach(_steps.Add);
        Logger.LogInfo("EWTest: " + runner.Describe() + " queued.");
    }

    private void PopulateMeta()
    {
        _report.Meta["apiVersion"] = ReadApiVersion();
        _report.Meta["gameVersion"] = Application.version ?? "";
        _report.Meta["unityVersion"] = Application.unityVersion ?? "";
        _report.Meta["gameAssemblySha256"] = ReadAssemblyHash() ?? "";
        _report.Meta["startedUtc"] = DateTime.UtcNow.ToString("O");
    }

    private static string ReadApiVersion()
    {
        try { return typeof(ModApi).Assembly.GetName().Version?.ToString(3) ?? "unknown"; }
        catch { return "unknown"; }
    }

    private static string? ReadAssemblyHash()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .SingleOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            var location = assembly?.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location)) return null;
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(location);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
        catch { return null; }
    }

    private void OnLifecycle(LifecycleEvent e)
    {
        lock (_lifecycleEvents)
        {
            _lifecycleEvents.Add(e);
            if (e.Kind == LifecycleEventKind.GameplayInitialized) _sessionReached = true;
        }
    }

    private void Update()
    {
        if (!_started || _finalized) return;
        int safety = 0;
        while (_steps.Count > 0 && safety++ < 64 && _steps[0]())
            _steps.RemoveAt(0);
        if (_steps.Count == 0) Finalize();
    }

    private void Finalize()
    {
        _finalized = true;
        _report.Meta["finishedUtc"] = DateTime.UtcNow.ToString("O");
        try
        {
            File.WriteAllText(_reportPath!, _report.ToJson());
            Logger.LogInfo("EWTest: report written to " + _reportPath);
        }
        catch (Exception ex)
        {
            Logger.LogError("EWTest: failed to write report: " + ex);
        }
        try { Application.Quit(); }
        catch (Exception ex) { Logger.LogError("EWTest: quit failed: " + ex); }
    }

    internal E2EReport Report => _report;
    internal bool Started => _started;
    internal ILifecycleService? Lifecycle => _lifecycle;
    internal bool SessionReached => _sessionReached;

    internal List<LifecycleEvent> LifecycleEvents
    {
        get { lock (_lifecycleEvents) return new List<LifecycleEvent>(_lifecycleEvents); }
    }
}

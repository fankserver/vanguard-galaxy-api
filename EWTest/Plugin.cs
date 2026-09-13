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
/// Development-only in-game E2E harness. It is inert unless launched by the driver with the
/// handshake (EWTEST_RUN=1 / the `-runEWTests` command-line argument) and an EWTEST_PORT. When so
/// launched it connects back to the driver's loopback listener and streams every check result over
/// newline-delimited JSON, then sends `finish` and quits. This mirrors the proven Surity in-game
/// pattern (batchmode launch + handshake + streamed results). EWTest is never shipped.
/// </summary>
[BepInPlugin(Id, "VGModAPI E2E harness", "1.0.0")]
[BepInDependency(ModApi.PluginId)]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "vgmodapi.devtools.e2e";
    internal const string HandshakeArg = "-runEWTests";
    private const string RunEnv = "EWTEST_RUN";
    private const string PortEnv = "EWTEST_PORT";
    internal const int MaxSessionWaitFrames = 18_000; // ~5 min at 60fps

    private readonly List<Func<bool>> _steps = new();
    private readonly List<LifecycleEvent> _lifecycleEvents = new();

    private ILifecycleService? _lifecycle;
    private WireSender? _wire;
    private bool _started;
    private bool _finalized;
    private bool _sessionReached;

    private void Awake()
    {
        bool handshaken = Environment.GetCommandLineArgs().Contains(HandshakeArg, StringComparer.Ordinal)
                          || Environment.GetEnvironmentVariable(RunEnv) == "1";
        if (!handshaken || !int.TryParse(Environment.GetEnvironmentVariable(PortEnv), out int port))
        {
            Logger.LogInfo("EWTest idle (not launched by the e2e driver).");
            return;
        }
        _started = true;

        _wire = WireSender.Connect(port);
        if (_wire == null)
        {
            Logger.LogError("EWTest could not connect to the e2e listener on 127.0.0.1:" + port);
        }
        else
        {
            _wire.Send(new Dictionary<string, object>
            {
                ["type"] = "meta",
                ["apiVersion"] = ReadApiVersion(),
                ["gameVersion"] = Application.version ?? "",
                ["unityVersion"] = Application.unityVersion ?? "",
                ["gameAssemblySha256"] = ReadAssemblyHash() ?? "",
                ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            });
        }

        try
        {
            _lifecycle = ModApi.Services.Lifecycle;
            _lifecycle.Changed += OnLifecycle;
        }
        catch (Exception ex)
        {
            Logger.LogError("EWTest could not subscribe to lifecycle: " + ex);
        }

        foreach (var step in new EWRunner(this).Build()) _steps.Add(step);
        Logger.LogInfo("EWTest: e2e suites queued; streaming to port " + port + ".");
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
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
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
        if (_steps.Count == 0) CompleteRun();
    }

    private void CompleteRun()
    {
        _finalized = true;
        try
        {
            _wire?.Send(new Dictionary<string, object> { ["type"] = "finish" });
            _wire?.Dispose();
        }
        catch (Exception ex) { Logger.LogError("EWTest: finish send failed: " + ex); }
        try { Application.Quit(); }
        catch (Exception ex) { Logger.LogError("EWTest: quit failed: " + ex); }
    }

    internal bool Started => _started;
    internal ILifecycleService? Lifecycle => _lifecycle;
    internal bool SessionReached => _sessionReached;

    internal List<LifecycleEvent> LifecycleEvents
    {
        get { lock (_lifecycleEvents) return new List<LifecycleEvent>(_lifecycleEvents); }
    }

    /// <summary>Stream one finished suite out over the wire (suite-start then one result line each).</summary>
    internal void StreamSuite(SuiteResult suite)
    {
        if (_wire == null) return;
        _wire.Send(new Dictionary<string, object>
        {
            ["type"] = "suite-start",
            ["suite"] = new Dictionary<string, object> { ["id"] = suite.Id, ["name"] = suite.Name },
        });
        foreach (var r in suite.Results)
        {
            _wire.Send(new Dictionary<string, object>
            {
                ["type"] = "result",
                ["check"] = new Dictionary<string, object>
                {
                    ["check"] = r.Check,
                    ["status"] = r.Status,
                    ["message"] = r.Message,
                    ["expected"] = r.Expected,
                    ["actual"] = r.Actual,
                    ["suggestedAction"] = r.SuggestedAction,
                    ["elapsedMs"] = r.ElapsedMs,
                },
            });
        }
    }
}

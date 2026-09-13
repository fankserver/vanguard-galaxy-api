using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx;
using UnityEngine;

namespace VGModAPI.E2E;

[BepInPlugin("vgmodapi.e2e", "VGModAPI E2E", "1.0.0")]
[BepInDependency(ModApi.PluginId)]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly Stopwatch _clock = new();
    private readonly List<LifecycleEvent> _events = new();
    private ILifecycleService? _lifecycle;
    private WireSender? _wire;
    private GameTest? _test;
    private bool _finished;

    private void Awake()
    {
        if (!Environment.GetCommandLineArgs().Contains("--vgmodapi-e2e")) return;
        try
        {
            if (!int.TryParse(Environment.GetEnvironmentVariable("VGMODAPI_E2E_PORT"), out var port)
                || port < 1 || port > 65535)
                throw new InvalidOperationException("Missing controller port.");
            var run = Environment.GetEnvironmentVariable("VGMODAPI_E2E_RUN");
            if (string.IsNullOrEmpty(run)) throw new InvalidOperationException("Missing controller run ID.");
            if (!long.TryParse(Environment.GetEnvironmentVariable("VGMODAPI_E2E_DEADLINE"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var deadline))
                throw new InvalidOperationException("Missing controller deadline.");
            if (Environment.GetEnvironmentVariable("VGMODAPI_E2E_CASE") != FreshSessionCase.Id)
                throw new InvalidOperationException("Unknown E2E case.");
            _wire = new WireSender(port);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(assembly.Location);
            _wire.Send(new { type = "meta", runId = run, apiVersion = typeof(ModApi).Assembly.GetName().Version?.ToString(),
                gameVersion = Application.version, unityVersion = Application.unityVersion,
                gameAssemblySha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() });
            _lifecycle = ModApi.Services.Lifecycle;
            _lifecycle.Changed += OnLifecycle;
            // Charge player startup against the controller's budget, then use a monotonic clock.
            var remaining = (deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
            _test = new GameTest(FreshSessionCase.Steps(_lifecycle, _events), remaining);
            _clock.Start();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            Complete(false, "Test bootstrap: " + ex.GetBaseException().Message, "Controller handshake / API bootstrap");
        }
    }

    private void OnLifecycle(LifecycleEvent value) => _events.Add(value);

    private void Update()
    {
        if (_test == null || _finished) return;
        _test.Tick(_clock.Elapsed.TotalSeconds);
        if (_test.Finished) Complete(_test.Passed, _test.Detail, _test.Binding);
    }

    private void Complete(bool passed, string detail, string binding)
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _wire?.Send(new { type = "result", id = FreshSessionCase.Id, status = passed ? "pass" : "fail",
                detail, binding, elapsedMs = _clock.ElapsedMilliseconds });
            _wire?.Send(new { type = "finish" });
        }
        catch (Exception ex) { Logger.LogError("E2E report failed: " + ex); }
        finally
        {
            Release();
            Application.Quit();
        }
    }

    private void Release()
    {
        if (_lifecycle != null) { _lifecycle.Changed -= OnLifecycle; _lifecycle = null; }
        try { _wire?.Dispose(); } catch (Exception ex) { Logger.LogWarning(ex.Message); }
        _wire = null;
    }

    private void OnDestroy() => Release();
}

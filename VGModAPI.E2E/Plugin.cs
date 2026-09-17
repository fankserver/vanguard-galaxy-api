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
    private readonly List<TravelTransition> _travelEvents = new();
    private ILifecycleService? _lifecycle;
    private ITravelService? _travel;
    private WireSender? _wire;
    private GameTest? _test;
    private string _caseId = "";
    private bool _finished;
    private string _loggedStep = "";
    private string _reportedObservation = "";
    private double _nextProgressAt;
    private bool _failureCaptureQueued;
    private string? _failureCapturePath;
    private double _failureCaptureDeadline;

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
            var requested = Environment.GetEnvironmentVariable("VGMODAPI_E2E_CASE");
            if (string.IsNullOrEmpty(requested)) throw new InvalidOperationException("Missing E2E case.");
            if (!long.TryParse(Environment.GetEnvironmentVariable("VGMODAPI_E2E_DEADLINE"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var deadline))
                throw new InvalidOperationException("Missing controller deadline.");
            _caseId = requested switch
            {
                EquipmentPolicyCase.Id => EquipmentPolicyCase.Id,
                EquipmentTargetingCase.Id => EquipmentTargetingCase.Id,
                ModSettingsMenuCase.Id => ModSettingsMenuCase.Id,
                FreshSessionCase.Id => FreshSessionCase.Id,
                PocketWorldsCase.Id => PocketWorldsCase.Id,
                StoryMissionsCase.Id => StoryMissionsCase.Id,
                ObservationCase.Id => ObservationCase.Id,
                CargoRecoveryCase.Id => CargoRecoveryCase.Id,
                StationCommerceCase.Id => StationCommerceCase.Id,
                UiSurfacesCase.Id => UiSurfacesCase.Id,
                _ => throw new InvalidOperationException("Unknown E2E case: " + requested),
            };
            _wire = new WireSender(port);
            var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(assembly.Location);
            _wire.Send(new { type = "meta", runId = run, apiVersion = typeof(ModApi).Assembly.GetName().Version?.ToString(),
                gameVersion = Application.version, unityVersion = Application.unityVersion,
                gameAssemblySha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() });
            _lifecycle = ModApi.Services.Lifecycle;
            _lifecycle.Changed += OnLifecycle;
            _travel = ModApi.Services.Travel;
            _travel.Transitioned += OnTravel;
            // Charge player startup against the controller's budget, then use a monotonic clock.
            var remaining = (deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000.0;
            _test = _caseId switch
            {
                EquipmentPolicyCase.Id => new GameTest(EquipmentPolicyCase.Steps(_lifecycle, _events), remaining),
                EquipmentTargetingCase.Id => new GameTest(EquipmentTargetingCase.Steps(_lifecycle, _events), remaining),
                ModSettingsMenuCase.Id => new GameTest(ModSettingsMenuCase.Steps(), remaining),
                FreshSessionCase.Id => new GameTest(FreshSessionCase.Steps(_lifecycle, _events), remaining),
                PocketWorldsCase.Id => new GameTest(PocketWorldsCase.Steps(_lifecycle, _events, _travelEvents), remaining),
                StoryMissionsCase.Id => new GameTest(StoryMissionsCase.Steps(_lifecycle, _events), remaining),
                ObservationCase.Id => new GameTest(ObservationCase.Steps(_lifecycle, _events), remaining),
                CargoRecoveryCase.Id => new GameTest(CargoRecoveryCase.Steps(_lifecycle, _events, _travelEvents), remaining),
                StationCommerceCase.Id => new GameTest(StationCommerceCase.Steps(_lifecycle, _events), remaining),
                UiSurfacesCase.Id => new GameTest(UiSurfacesCase.Steps(_lifecycle, _events), remaining),
                _ => throw new InvalidOperationException("Unhandled case: " + _caseId),
            };
            _clock.Start();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            Complete(false, "Test bootstrap: " + ex.GetBaseException().Message, "Controller handshake / API bootstrap");
        }
    }

    private void OnLifecycle(LifecycleEvent value) => _events.Add(value);
    private void OnTravel(TravelTransition value) => _travelEvents.Add(value);

    private void Update()
    {
        if (_test == null || _finished) return;
        var now = _clock.Elapsed.TotalSeconds;
        var changedStep = !_test.Finished && _test.CurrentStep != _loggedStep;
        if (changedStep)
        {
            _loggedStep = _test.CurrentStep;
            _reportedObservation = "";
            _nextProgressAt = 0;
            Logger.LogInfo("E2E step: " + _loggedStep);
        }
        _test.Tick(now);
        if (!_test.Finished && _test.Observation != "not polled yet"
            && (changedStep || now >= _nextProgressAt && _test.Observation != _reportedObservation))
        {
            _reportedObservation = _test.Observation;
            _nextProgressAt = now + 5;
            _wire?.Send(new { type = "progress", id = _caseId, step = _test.CurrentStep,
                binding = _test.Binding, observation = _test.Observation,
                elapsedMs = (long)(_test.StepElapsedSeconds * 1000), timeoutMs = (long)(_test.StepTimeoutSeconds * 1000) });
        }
        if (_test.Finished)
        {
            if (!_test.Passed && !_failureCaptureQueued)
            {
                _failureCaptureQueued = true;
                _failureCapturePath = NativeGameplay.Screenshot("failure-" + _loggedStep);
                _failureCaptureDeadline = now + 2;
                return;
            }
            if (!_test.Passed && _failureCapturePath != null
                && !File.Exists(_failureCapturePath) && now < _failureCaptureDeadline) return;
            Complete(_test.Passed, _test.Detail, _test.Binding);
        }
    }

    private void Complete(bool passed, string detail, string binding)
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _wire?.Send(new { type = "result", id = _caseId, status = passed ? "pass" : "fail",
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
        if (_travel != null) { _travel.Transitioned -= OnTravel; _travel = null; }
        try { _wire?.Dispose(); } catch (Exception ex) { Logger.LogWarning(ex.Message); }
        _wire = null;
    }

    private void OnDestroy() => Release();
}

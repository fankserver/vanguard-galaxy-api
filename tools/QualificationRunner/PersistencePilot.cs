using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private ISaveDataRegistration? _alpha, _beta;
    private byte _alphaState = 1, _betaState = 2;
    private int _alphaRestores, _betaRestores;

    private void ArmPersistencePilot()
    {
        var marker = Path.Combine(_root, "persistence-probe.enabled");
        if (!File.Exists(marker)) return;
        var saveData = ModApi.Services.SaveData;
        Require(File.ReadAllText(marker).Trim() == "probe-v1" && saveData.Availability.IsAvailable, "Persistence probe unavailable.");
        _alpha = saveData.Register(new PersistenceProvider("qualification.alpha", 1,
            () => new[] { _alphaState }, (_, bytes) => { _alphaRestores++; _alphaState = bytes == null ? (byte)1 : bytes[0]; }, bytes => bytes.Length == 1)).Registration
            ?? throw new InvalidOperationException("Alpha provider registration refused.");
        try
        {
            _beta = saveData.Register(new PersistenceProvider("qualification.beta", 1,
                () => new[] { _betaState }, (_, bytes) => { _betaRestores++; _betaState = bytes == null ? (byte)2 : bytes[0]; }, bytes => bytes.Length == 1)).Registration
                ?? throw new InvalidOperationException("Beta provider registration refused.");
        }
        catch { _alpha.Dispose(); _alpha = null; throw; }
    }

    private IEnumerable<object?> PersistencePilot()
    {
        if (_alpha == null) yield break;
        Require(_alpha.CanMutate && _beta!.CanMutate, "Persistence owners not ready after ordinary load.");
        _alphaState = 11; _betaState = 22;
        Save("qa-coordinated", LifecycleEventKind.SaveSucceeded);
        Require(_alpha.CanMutate && _beta!.CanMutate, "Coordinated publication blocked: " + _alpha.State.Kind + "/" + _beta?.State.Kind);
        int alphaRestores = _alphaRestores, betaRestores = _betaRestores;
        _alphaState = 33; _betaState = 44;
        foreach (var frame in LoadReady("qa-coordinated")) yield return frame;
        Require(_alphaRestores == alphaRestores + 1 && _betaRestores == betaRestores + 1 && _alphaState == 11 && _betaState == 22,
            "Coordinated roundtrip did not restore both captured owners.");
        Require(_alpha.CanMutate && _beta!.CanMutate, "Roundtrip mutation gate did not reopen.");
        _alpha.Dispose();
        Require(!_alpha.CanMutate && !_beta!.CanMutate, "Active provider removal did not pause persistence.");
        Save("qa-coordinated-removed", LifecycleEventKind.SaveSucceeded);
        betaRestores = _betaRestores;
        foreach (var frame in LoadReady("qa-coordinated-removed")) yield return frame;
        Require(_betaRestores == betaRestores && !_beta!.CanMutate && _beta.State.Kind == SaveDataStateKind.Blocked && _beta.State.Reason == SaveDataBlockReason.LoadRefused, "Removed-provider save silently restored empty state.");
        _beta!.Dispose();
        File.WriteAllText(Path.Combine(_root, "persistence-probe.txt"), "PASS\nTwo synthetic owners: native coordinated save/reload, mutation gates, removal and durable missing-generation refusal. Not actual consumer migration qualification.");
        Passed("native-coordinated-save-data-and-removal");
    }
}

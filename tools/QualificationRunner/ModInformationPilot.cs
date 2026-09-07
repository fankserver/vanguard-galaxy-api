using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private CancellationTokenSource? _updateProbeStop;
    private IEnumerator RunModInformationProbe()
    {
        Require(File.ReadAllText(Path.Combine(_root!, "mod-information-probe.enabled")) == "mod-information-probe-v2", "Invalid information probe marker.");
        foreach (var frame in Wait(() => GameObject.Find("VGModAPI Mods") != null, "information probe main menu")) yield return frame;
        var revisions = Regex.Matches(File.ReadAllText(Path.Combine(_root!, "build-provenance.json")), "\"revision\"\\s*:\\s*\"([0-9a-f]{40})\"");
        Require(revisions.Count == 1, "Information probe requires one exact build revision.");
        var thread = Thread.CurrentThread.ManagedThreadId;
        var evidence = new StringBuilder("Mod information qualification v2\n");
        void Record(string step)
        {
            Require(Thread.CurrentThread.ManagedThreadId == thread, "Qualification continuation left the Unity thread.");
            evidence.AppendLine(step + "=PASS");
        }
        using var lifetime = new CancellationTokenSource();
        _updateProbeStop = lifetime;
        var token = lifetime.Token;
        void LiveRecord(string step) { token.ThrowIfCancellationRequested(); Record(step); }
        try
        {
            var controlled = ModUpdateChecks.ControlledAsync(Path.Combine(_root!, "update-check-cache"), LiveRecord, token);
            while (!controlled.IsCompleted) yield return null;
            controlled.GetAwaiter().GetResult();
            var heartbeat = new ProbeHeartbeat(Time.frameCount, Time.realtimeSinceStartup);
            var wire = ModUpdateWireChecks.RunAsync(revisions[0].Groups[1].Value, LiveRecord, token);
            while (!wire.IsCompleted) { heartbeat.Tick(Time.realtimeSinceStartup); yield return null; }
            wire.GetAwaiter().GetResult();
            heartbeat.Complete(Time.frameCount, Time.realtimeSinceStartup);
            var failures = ModUpdateWireFailures.RunAsync(Path.Combine(_root!, "untrusted-test.pfx"), LiveRecord, token);
            while (!failures.IsCompleted) { heartbeat.Tick(Time.realtimeSinceStartup); yield return null; }
            failures.GetAwaiter().GetResult();
            heartbeat.Tick(Time.realtimeSinceStartup);
            Require(GameObject.Find("VGModAPI Mods") != null, "Network checks destroyed the menu.");
            LiveRecord("unity-main-thread-menu-responsive");
        }
        finally { lifetime.Cancel(); _updateProbeStop = null; }
        var menu = RunModMenuProbe();
        try { while (menu.MoveNext()) yield return menu.Current; }
        finally { (menu as IDisposable)?.Dispose(); }
        var bytes = Encoding.UTF8.GetBytes(evidence.ToString());
        File.WriteAllBytes(Path.Combine(_root!, "mod-information-probe.txt"), bytes);
        using var hash = SHA256.Create();
        var digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        File.WriteAllText(Path.Combine(_root!, "mod-information-probe.receipt"), "PASS\nmod-information-probe-v2\nsha256=" + digest + "\n");
        Passed("mod-information-native-updates");
    }
}

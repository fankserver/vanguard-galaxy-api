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
    private IEnumerator RunModInformationProbe()
    {
        Require(File.ReadAllText(Path.Combine(_root!, "mod-information-probe.enabled")) == "mod-information-probe-v1", "Invalid information probe marker.");
        foreach (var frame in Wait(() => GameObject.Find("VGModAPI Mods") != null, "information probe main menu")) yield return frame;
        var revisions = Regex.Matches(File.ReadAllText(Path.Combine(_root!, "build-provenance.json")), "\"revision\"\\s*:\\s*\"([0-9a-f]{40})\"");
        Require(revisions.Count == 1, "Information probe requires one exact build revision.");
        var thread = Thread.CurrentThread.ManagedThreadId;
        var evidence = new StringBuilder("Mod information qualification v1\n");
        void Record(string step)
        {
            Require(Thread.CurrentThread.ManagedThreadId == thread, "Qualification continuation left the Unity thread.");
            evidence.AppendLine(step + "=PASS");
        }
        var frames = Time.frameCount;
        var controlled = ModUpdateChecks.ControlledAsync(Path.Combine(_root!, "update-check-cache"), Record);
        while (!controlled.IsCompleted) yield return null;
        controlled.GetAwaiter().GetResult();
        var wire = ModUpdateWireChecks.RunAsync(revisions[0].Groups[1].Value, Record);
        while (!wire.IsCompleted) yield return null;
        wire.GetAwaiter().GetResult();
        Require(Time.frameCount > frames + 2 && GameObject.Find("VGModAPI Mods") != null, "Network checks blocked or destroyed the menu.");
        Record("unity-main-thread-menu-responsive");
        var menu = RunModMenuProbe();
        try { while (menu.MoveNext()) yield return menu.Current; }
        finally { (menu as IDisposable)?.Dispose(); }
        var bytes = Encoding.UTF8.GetBytes(evidence.ToString());
        File.WriteAllBytes(Path.Combine(_root!, "mod-information-probe.txt"), bytes);
        using var hash = SHA256.Create();
        var digest = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        File.WriteAllText(Path.Combine(_root!, "mod-information-probe.receipt"), "PASS\nmod-information-probe-v1\nsha256=" + digest + "\n");
        Passed("mod-information-native-updates");
    }
}

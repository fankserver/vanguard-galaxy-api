using System;
using System.Globalization;
using System.Collections.Generic;
using VGModAPI.Core;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;

namespace VGModAPI;

[BepInDependency("vgmodapi.qualification.guard", "0.1.0")]
public sealed partial class Plugin { }

// Compiled only by the isolated candidate project. The launcher must supply the reviewed hash manifest.
internal sealed class QualificationRunContext
{
    private readonly string _root, _authorization;
    private readonly byte[] _bytes;
    private readonly string[] _hashes;
    private readonly DateTimeOffset _expires;
    private readonly object _guard;
    private readonly FieldInfo _armed, _savePath, _saveDirectory;
    private readonly string _gameAssembly;
    private readonly FieldInfo _nativePointer;
    private readonly IntPtr _guardPointer;
    private readonly Dictionary<string, (object Instance, Assembly Assembly, IntPtr Pointer)> _providers = new(StringComparer.Ordinal);
    private (object Instance, Assembly Assembly, IntPtr Pointer)? _runner;
    private bool _refused;
    private static readonly string[] Files = { "VGModAPI.dll", "VGModAPI.Core.dll", "VGModAPI.Abstractions.dll", "QualificationGuard.dll", "WorldAuthorA.dll", "WorldAuthorB.dll", "QualificationRunner.dll" };

    internal QualificationRunContext(Assembly game)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InvalidDataException("World qualification requires the inspected Windows sandbox.");
        var args = Environment.GetCommandLineArgs();
        string Argument(string flag)
        {
            int index = Array.IndexOf(args, flag);
            if (index < 0 || index + 1 >= args.Length || Array.LastIndexOf(args, flag) != index) throw new InvalidDataException("Missing or repeated qualification argument.");
            return args[index + 1];
        }
        _root = Path.GetFullPath(Argument("--vgmodapi-qualification-root"));
        string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        string name = Path.GetFileName(_root);
        if (!Same(Path.GetDirectoryName(_root)!, parent) || !name.StartsWith("VGModAPI-qa-", StringComparison.Ordinal) ||
            !int.TryParse(name.Substring(12), NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number <= 0)
            throw new InvalidDataException("Not a numbered disposable sandbox.");
        RequireUnlinked(_root);
        _authorization = Path.Combine(_root, "world-qualification.authorization");
        _bytes = ReadBounded(_authorization);
        var authorization = QualificationAuthorization.Parse(_bytes, Argument("--vgmodapi-world-run"), DateTimeOffset.UtcNow);
        _expires = authorization.Expires; _hashes = authorization.Hashes;
        _gameAssembly = game.Location;
        var save = game.GetType("Source.Util.SaveGame", true)!;
        _savePath = save.GetField("SavesPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        _saveDirectory = save.GetField("SavesDir", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        _guard = Chainloader.PluginInfos["vgmodapi.qualification.guard"].Instance;
        _armed = _guard.GetType().GetField("_armed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _nativePointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        _guardPointer = (IntPtr)_nativePointer.GetValue(_guard)!;
        if (!IsCurrent()) throw new InvalidDataException("World qualification context is unavailable.");
    }
    internal bool Allows(string owner) => owner == "vgmodapi.qualification.world.a" || owner == "vgmodapi.qualification.world.b";
    internal StoryHostPlugin? Authenticate(object instance, Assembly caller)
    {
        var authenticated = StoryHostAuthentication.Resolve(instance, caller);
        if (authenticated == null || !Allows(authenticated.PluginId) || !IsCurrent()) return null;
        string file = authenticated.PluginId == "vgmodapi.qualification.world.a" ? "WorldAuthorA.dll" : "WorldAuthorB.dll";
        if (!Same(caller.Location, Path.Combine(_root, "game", "BepInEx", "plugins", file))) return null;
        var pointer = (IntPtr)_nativePointer.GetValue(instance)!;
        if (pointer == IntPtr.Zero) return null;
        if (_providers.TryGetValue(authenticated.PluginId, out var previous) &&
            (!ReferenceEquals(previous.Instance, instance) || !ReferenceEquals(previous.Assembly, caller) || previous.Pointer != pointer)) return null;
        _providers[authenticated.PluginId] = (instance, caller, pointer);
        return authenticated;
    }
    internal bool ParticipantsReady()
    {
        if (!IsCurrent() || _providers.Count != 2) return false;
        if (!Chainloader.PluginInfos.TryGetValue("vgmodapi.qualification", out var info) || ReferenceEquals(info.Instance, null)) return false;
        var assembly = info.Instance.GetType().Assembly;
        var pointer = (IntPtr)_nativePointer.GetValue(info.Instance)!;
        if (pointer == IntPtr.Zero || !Same(assembly.Location, Path.Combine(_root, "game", "BepInEx", "plugins", "QualificationRunner.dll")))
        { _refused = true; return false; }
        _runner ??= (info.Instance, assembly, pointer);
        return IsCurrent();
    }
    internal bool IsCurrent()
    {
        if (_refused) return false;
        try
        {
            RequireUnlinked(_root);
            string gameRoot = Path.Combine(_root, "game"), saves = Path.Combine(_root, "Saves"), plugins = Path.Combine(gameRoot, "BepInEx", "plugins");
            RequireUnlinked(gameRoot); RequireUnlinked(saves); RequireUnlinked(plugins);
            var permitted = new HashSet<string>(Files, StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(plugins))
                if (!permitted.Remove(Path.GetFileName(entry)) || (File.GetAttributes(entry) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("Unexpected plugin profile entry.");
            if (permitted.Count != 0) throw new InvalidDataException("Incomplete plugin profile.");
            foreach (var id in Chainloader.PluginInfos.Keys)
                if (id != "vgmodapi" && id != "vgmodapi.qualification.guard" && id != "vgmodapi.qualification" && !Allows(id))
                    throw new InvalidDataException("Unexpected loaded plugin.");
            foreach (var entry in _providers)
                if (!Chainloader.PluginInfos.TryGetValue(entry.Key, out var info) || !ReferenceEquals(info.Instance, entry.Value.Instance) ||
                    !ReferenceEquals(info.Instance.GetType().Assembly, entry.Value.Assembly) || (IntPtr)_nativePointer.GetValue(entry.Value.Instance)! != entry.Value.Pointer)
                    throw new InvalidDataException("Authenticated world provider changed.");
            if (_runner is { } runner && (!Chainloader.PluginInfos.TryGetValue("vgmodapi.qualification", out var runnerInfo) ||
                !ReferenceEquals(runnerInfo.Instance, runner.Instance) || !ReferenceEquals(runnerInfo.Instance.GetType().Assembly, runner.Assembly) ||
                (IntPtr)_nativePointer.GetValue(runner.Instance)! != runner.Pointer)) throw new InvalidDataException("Qualification runner changed.");
            if (Encoding.UTF8.GetString(ReadBounded(Path.Combine(_root, "qualification.marker"))).Trim() != "vgmodapi-disposable-sandbox-v1" ||
                Encoding.UTF8.GetString(ReadBounded(Path.Combine(_root, "scenario.txt"))).Trim() != "Full" ||
                DateTimeOffset.UtcNow >= _expires || !(bool)_armed.GetValue(_guard)! || _guardPointer == IntPtr.Zero || (IntPtr)_nativePointer.GetValue(_guard)! != _guardPointer ||
                !ReferenceEquals(Chainloader.PluginInfos["vgmodapi.qualification.guard"].Instance, _guard) ||
                !Same(Path.GetDirectoryName(UnityEngine.Application.dataPath)!, gameRoot) ||
                !Same((string)_savePath.GetValue(null)!, saves) || !Same(((DirectoryInfo)_saveDirectory.GetValue(null)!).FullName, saves) ||
                !ReadBounded(_authorization).SequenceEqual(_bytes)) throw new InvalidDataException("Qualification context changed.");
            if (Hash(_gameAssembly) != _hashes[0]) throw new InvalidDataException("Game assembly changed.");
            for (int i = 0; i < Files.Length; i++)
            {
                string file = Path.Combine(plugins, Files[i]); RequireUnlinked(file);
                if (Hash(file) != _hashes[i + 1]) throw new InvalidDataException("Candidate package changed.");
            }
            if (!Same(typeof(Plugin).Assembly.Location, Path.Combine(plugins, Files[0])) ||
                !Same(typeof(Core.LifecycleHub).Assembly.Location, Path.Combine(plugins, Files[1])) ||
                !Same(typeof(ModApi).Assembly.Location, Path.Combine(plugins, Files[2])) ||
                !Same(_guard.GetType().Assembly.Location, Path.Combine(plugins, Files[3]))) throw new InvalidDataException("Candidate assembly location mismatch.");
            return true;
        }
        catch { _refused = true; return false; }
    }
    private static bool Same(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static void RequireUnlinked(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked qualification path.");
    }
    private static byte[] ReadBounded(string path)
    {
        RequireUnlinked(path);
        using var stream = File.OpenRead(path); using var buffer = new MemoryStream();
        for (int i = 0; i <= 4096; i++)
        {
            int value = stream.ReadByte(); if (value < 0) return buffer.ToArray();
            if (i == 4096) throw new InvalidDataException("Oversized authorization.");
            buffer.WriteByte((byte)value);
        }
        throw new InvalidDataException("Invalid authorization.");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path); using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}

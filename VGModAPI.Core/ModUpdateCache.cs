using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VGModAPI.Core;

// Private, bounded cache outside game saves. Failures are deliberately non-fatal.
internal sealed class ModUpdateCache
{
    private readonly string _directory;
    private readonly object _gate = new object();
    internal ModUpdateCache(string directory) { _directory = directory; }
    internal static string Key(ModInformation mod)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes("1\n" + mod.PluginId + "\n" + mod.Metadata?.UpdateUrl + "\n" + mod.Metadata?.Channel))).Replace("-", "").ToLowerInvariant();
    }
    internal (ModUpdateFeed Feed, DateTimeOffset Time)? Read(ModInformation mod, DateTimeOffset now)
    {
        lock (_gate)
        try
        {
            var path = Path.Combine(_directory, Key(mod) + ".cache");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return null;
            using var file = File.OpenRead(path);
            if (file.Length < 9 || file.Length > ModMetadataCodec.MaxBytes + 8) return null;
            using var reader = new BinaryReader(file);
            var time = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            if (time > now || now - time > TimeSpan.FromDays(30)) return null;
            var bytes = reader.ReadBytes((int)file.Length - 8);
            return (ModUpdateFeed.Parse(bytes, mod.PluginId, mod.Metadata!.Channel), time);
        }
        catch (Exception) { return null; }
    }
    internal void Write(ModInformation mod, ModUpdateFeed feed, DateTimeOffset time)
    {
        lock (_gate)
        try
        {
            Directory.CreateDirectory(_directory);
            if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) return;
            var paths = Directory.EnumerateFiles(_directory, "*.cache").Take(129).ToArray();
            if (paths.Length > 128) return;
            var path = Path.Combine(_directory, Key(mod) + ".cache");
            if (!File.Exists(path) && paths.Length >= 128)
                File.Delete(paths.OrderBy(File.GetLastWriteTimeUtc).First());
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
            var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"pluginId\":" + Quote(mod.PluginId) + ",\"channel\":" + Quote(mod.Metadata!.Channel) +
                ",\"version\":" + Quote(feed.Version.ToString()) + ",\"releaseUrl\":" + Quote(feed.ReleaseUrl) + "}");
            if (bytes.Length > ModMetadataCodec.MaxBytes) return;
            // CreateNew never follows an existing temporary-file link.
            var temporary = path + ".tmp";
            var created = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    using var writer = new BinaryWriter(file);
                    writer.Write(time.UtcTicks); writer.Write(bytes);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (created && File.Exists(temporary) && (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) == 0) File.Delete(temporary); }
        }
        catch (Exception) { /* Cache failure never changes a successful network result. */ }
    }
    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var c in text)
        {
            if (c == '"' || c == '\\') result.Append('\\').Append(c);
            else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
            else result.Append(c);
        }
        return result.Append('"').ToString();
    }
}

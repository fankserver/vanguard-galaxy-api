using System;
using System.IO;
using System.Security.Cryptography;

namespace VGModAPI.Core;

internal sealed class PersistenceFiles
{
    private readonly string _saves;
    internal PersistenceFiles(string saves)
    {
        _saves = Normalize(saves).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectLinks(_saves);
        if ((File.GetAttributes(_saves) & FileAttributes.Directory) == 0) throw new IOException("Save root must be a directory.");
    }

    private static string Normalize(string path)
    {
        if (!Path.IsPathRooted(path)) throw new ArgumentException("Absolute persistence path required.");
        var full = Path.GetFullPath(path);
        return Path.DirectorySeparatorChar == '\\' ? full.ToUpperInvariant() : full;
    }

    internal string Canonical(string path)
    {
        var full = Normalize(path);
        if (Path.GetDirectoryName(full) != _saves || !full.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full).Contains("~")) throw new IOException("Save path is outside the supported canonical save directory.");
        RejectLinks(full);
        return full;
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Save paths must not traverse links.");
        }
    }

    internal string HashFile(string path)
    {
        path = Canonical(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
    }
}

using System;
using System.IO;

namespace VGModAPI.Core;

/// <summary>Single bounded read of the observed source. A load adapter must parse these returned bytes, not reopen the path.</summary>
internal static class WorldLoadFile
{
    internal static byte[] Capture(string canonicalPath, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(canonicalPath) || !Path.IsPathRooted(canonicalPath) || Path.GetFullPath(canonicalPath) != canonicalPath)
            throw new InvalidDataException("Canonical world load path required.");
        using var input = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > WorldLoadBytes.MaxNativeBytes) throw new InvalidDataException("Native load file exceeds its bound.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > WorldLoadBytes.MaxNativeBytes) throw new InvalidDataException("Native load file grew beyond its bound.");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (!string.Equals(GenerationStore.Hash(bytes), expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException("Native load bytes changed after the observed session start.");
        return bytes;
    }
}

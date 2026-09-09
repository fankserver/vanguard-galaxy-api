using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Bounded decoding of the actual compressed or raw native load bytes; never substitutes a later file read.</summary>
internal static class WorldLoadBytes
{
    internal const int MaxNativeBytes = 64 * 1024 * 1024;
    internal const int MaxDecodedBytes = 256 * 1024 * 1024;
    internal static string Decode(byte[] bytes, int decodedLimit = MaxDecodedBytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > MaxNativeBytes || decodedLimit < 1 || decodedLimit > MaxDecodedBytes)
            throw new InvalidDataException("Invalid native world input bounds.");
        byte[] decoded;
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var input = new MemoryStream(bytes, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > decodedLimit) throw new InvalidDataException("Decompressed world input exceeds its bound.");
                output.Write(buffer, 0, count);
            }
            decoded = output.ToArray();
        }
        else
        {
            if (bytes.Length > decodedLimit) throw new InvalidDataException("Native world input exceeds its decoded bound.");
            decoded = bytes;
        }
        try { return new UTF8Encoding(false, true).GetString(decoded); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid UTF-8 in native world input.", error); }
    }
}

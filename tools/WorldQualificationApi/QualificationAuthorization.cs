using System;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;

namespace VGModAPI;

internal sealed class QualificationAuthorization
{
    internal DateTimeOffset Expires { get; }
    internal string[] Hashes { get; }
    private QualificationAuthorization(DateTimeOffset expires, string[] hashes) { Expires = expires; Hashes = hashes; }
    internal static QualificationAuthorization Parse(byte[] bytes, string run, DateTimeOffset now)
    {
        if (bytes.Length > 4096) throw new InvalidDataException("Oversized world authorization.");
        var lines = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length != 11 || lines[0] != "vgmodapi-world-empty-v1" || !Guid.TryParseExact(lines[1], "D", out _) || lines[1] != run ||
            !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out long expiry)) throw new InvalidDataException("Invalid world authorization.");
        var expires = DateTimeOffset.FromUnixTimeSeconds(expiry);
        if (expires <= now || expires > now.AddHours(4)) throw new InvalidDataException("World authorization is outside its lifetime.");
        var hashes = lines.Skip(3).ToArray();
        if (hashes.Any(hash => hash.Length != 64 || hash.Any(character => !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')))) throw new InvalidDataException("Invalid candidate digest.");
        return new QualificationAuthorization(expires, hashes);
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace VGModAPI.Core;

/// <summary>Strict bounded flat JSON for schema 1; no game-provided serializer dependency.</summary>
internal static class ModMetadataCodec
{
    internal const int MaxBytes = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ModAuthorMetadata Parse(byte[] bytes, string pluginId)
    {
        if (bytes.Length > MaxBytes) throw new FormatException("Metadata is oversized.");
        var fields = new FlatObject(Utf8.GetString(bytes)).Read();
        if (!fields.TryGetValue("schemaVersion", out var schema) || schema != "1" ||
            !fields.TryGetValue("pluginId", out var id) || id != pluginId)
            throw new FormatException("Metadata schema or identity mismatch.");
        foreach (var key in fields.Keys)
            if (key is not ("schemaVersion" or "pluginId" or "author" or "description" or "projectUrl" or "updateUrl" or "channel"))
                throw new FormatException("Unknown metadata field.");
        var author = Optional(fields, "author", 256);
        var description = Optional(fields, "description", 4096);
        var project = Optional(fields, "projectUrl", 2048);
        var update = Optional(fields, "updateUrl", 2048);
        if ((project != null && !IsPublicHttpsUrl(project)) || (update != null && !IsPublicHttpsUrl(update)))
            throw new FormatException("Invalid metadata URL.");
        var channel = Optional(fields, "channel", 32) ?? "stable";
        if (channel is not ("stable" or "experimental")) throw new FormatException("Invalid channel.");
        return new ModAuthorMetadata(author, description, project, update, channel);
    }

    private static string? Optional(Dictionary<string, string> fields, string key, int limit)
    {
        if (!fields.TryGetValue(key, out var value)) return null;
        if (value.Length == 0 || value.Length > limit) throw new FormatException("Invalid field length.");
        foreach (var c in value)
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) throw new FormatException("Invalid control character.");
        return value;
    }

    // Syntactic policy only. A DNS name can resolve privately; the network layer must apply its own host policy.
    internal static bool IsPublicHttpsUrl(string value)
    {
        if (value.Length > 2048 || value.IndexOf('\\') >= 0) return false;
        foreach (var c in value) if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort) return false;
        var host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || host.IndexOf('.') < 0) return false;
        foreach (var suffix in new[] { ".localhost", ".local", ".internal", ".test", ".invalid" })
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    internal static bool TryVersion(string text, out Version? version)
    {
        version = null;
        if (text.Length > 43) return false;
        foreach (var c in text) if ((c < '0' || c > '9') && c != '.') return false;
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private sealed class FlatObject
    {
        private readonly string _text;
        private int _at;
        internal FlatObject(string text) => _text = text;
        internal Dictionary<string, string> Read()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            Expect('{');
            if (!Take('}'))
            {
                do
                {
                    var key = String(); Expect(':');
                    string value;
                    if (key == "schemaVersion") { Expect('1'); value = "1"; }
                    else value = String();
                    if (!result.TryAdd(key, value) || result.Count > 7) throw new FormatException("Duplicate or excessive metadata fields.");
                } while (Take(','));
                Expect('}');
            }
            White();
            if (_at != _text.Length) throw new FormatException("Trailing JSON content.");
            return result;
        }
        private string String()
        {
            Expect('"'); var value = new StringBuilder();
            while (_at < _text.Length)
            {
                var c = _text[_at++];
                if (c == '"')
                {
                    var result = value.ToString();
                    _ = Utf8.GetByteCount(result); // Reject unpaired JSON surrogate escapes.
                    return result;
                }
                if (c < 32) throw new FormatException("Unescaped JSON control character.");
                if (c == '\\')
                {
                    if (_at == _text.Length) throw new FormatException("Incomplete JSON escape.");
                    c = _text[_at++] switch
                    {
                        '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
                        'n' => '\n', 'r' => '\r', 't' => '\t', 'u' => Unicode(),
                        _ => throw new FormatException("Invalid JSON escape.")
                    };
                }
                value.Append(c);
                if (value.Length > 4096) throw new FormatException("JSON string is oversized.");
            }
            throw new FormatException("Unterminated JSON string.");
        }
        private char Unicode()
        {
            var value = 0;
            for (var i = 0; i < 4; ++i)
            {
                if (_at == _text.Length) throw new FormatException("Incomplete Unicode escape.");
                var c = _text[_at++];
                var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0) throw new FormatException("Invalid Unicode escape.");
                value = value * 16 + digit;
            }
            return (char)value;
        }
        private void White() { while (_at < _text.Length && _text[_at] is ' ' or '\t' or '\r' or '\n') ++_at; }
        private bool Take(char c) { White(); if (_at == _text.Length || _text[_at] != c) return false; ++_at; return true; }
        private void Expect(char c) { if (!Take(c)) throw new FormatException("Unexpected JSON token."); }
    }
}

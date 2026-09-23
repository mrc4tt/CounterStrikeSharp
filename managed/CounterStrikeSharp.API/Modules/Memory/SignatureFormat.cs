using System;
using System.Text;

namespace CounterStrikeSharp.API.Modules.Memory;

internal static class SignatureFormat
{
    /// <summary>
    /// Renders a signature IDA-style ("48 8B ? ?") for log output. Code-style input
    /// ("\x48\x8B\x2A") is converted with the same rules the native scanner parses it by,
    /// so <c>\x2A</c> shows as a wildcard. Anything else is returned unchanged.
    /// </summary>
    public static string ToIdaStyle(string signature)
    {
        if (string.IsNullOrEmpty(signature) || signature[0] != '\\') return signature;

        var parts = signature.Split(@"\x", StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder(parts.Length * 3);

        foreach (var part in parts)
        {
            if (part.Length < 2) return signature;

            if (result.Length > 0) result.Append(' ');

            if (part.StartsWith("2A", StringComparison.Ordinal))
            {
                result.Append('?');
                continue;
            }

            if (!Uri.IsHexDigit(part[0]) || !Uri.IsHexDigit(part[1])) return signature;

            result.Append(char.ToUpperInvariant(part[0])).Append(char.ToUpperInvariant(part[1]));
        }

        return result.ToString();
    }
}

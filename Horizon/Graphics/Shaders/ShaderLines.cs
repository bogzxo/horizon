using System.Security.Cryptography;
using System.Text;

namespace Horizon.Graphics;

/// <summary>
/// Shader text with one kind of line ending. A file saved on Windows with \r\n and the same file read out of an APK
/// with \n have to give the same key, or a pack built on one machine misses on the other. So everything the
/// preprocessor reads is split here and joined back with \n alone.
/// </summary>
internal static class ShaderLines
{
    /// <summary>The lines of some text, split on \r\n, \r or \n, without a byte order mark and without an empty last line for a file that ends on a line break.</summary>
    public static string[] Split(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line) lines.Add(line);
        return [.. lines];
    }

    /// <summary>The same text with \n between its lines and nothing else.</summary>
    public static string Canonical(string text) => string.Join('\n', Split(text));

    /// <summary>Normalises line endings while keeping every trailing blank line for source stored in a manifest.</summary>
    public static string NormalizeLineEndings(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>A hex SHA-256 of some text as UTF-8, what the manifest says a file or a program was.</summary>
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

using System.Text;

namespace ArcaneCore.Kernel.Configuration.Conf;

/// <summary>
/// One <c>Key = Value</c> line of a .conf file: <see cref="Key"/> as written, <see cref="Path"/> as the configuration path
/// (<c>World.Port</c> is <c>World:Port</c>), the raw <see cref="Value"/>, its 1-based <see cref="Line"/> and whether the
/// value was written in double quotes.
/// </summary>
public sealed record ConfEntry(string Key, string Path, string Value, int Line, bool Quoted);

/// <summary>A .conf file with at least one error. Every error of the file is listed, each as <c>file:line: message</c>.</summary>
public sealed class ConfParseException : Exception
{
    public ConfParseException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }

    /// <summary>The errors, each <c>file:line: message</c>.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// A parsed .conf file (docs/guide/configuration.md). Lines are blank, a comment (first non-blank character <c>#</c> or <c>;</c>)
/// or <c>Key = Value</c>. There are no inline comments, because connection strings and passwords contain <c>#</c> and <c>;</c>.
/// A value is trimmed, or double-quoted (only <c>\"</c> and <c>\\</c> are escapes, so Windows paths survive). A key's
/// <c>.</c> separates configuration path segments; a key that contains <c>:</c> is taken verbatim. The parser stores raw
/// strings exactly as <c>IConfiguration</c> holds them; typing is checked against the catalog (<see cref="ConfKeyCheck"/>).
/// Instances are immutable.
/// </summary>
public sealed class ConfDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private ConfDocument(string fileName, IReadOnlyList<ConfEntry> entries)
    {
        FileName = fileName;
        Entries = entries;
    }

    /// <summary>The file name used in messages (no directory).</summary>
    public string FileName { get; }

    public IReadOnlyList<ConfEntry> Entries { get; }

    /// <summary>Reads and parses a file; throws <see cref="ConfParseException"/> on any error and <see cref="IOException"/> when it cannot be read.</summary>
    public static ConfDocument Load(string path) => Parse(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>Parses UTF-8 bytes (a BOM is allowed; invalid UTF-8 is an error naming its line).</summary>
    public static ConfDocument Parse(byte[] bytes, string fileName)
    {
        var lines = new List<string>();
        var errors = new List<string>();
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        int lineNumber = 1;
        while (start < bytes.Length)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', start);
            int next = end < 0 ? bytes.Length : end + 1;
            if (end < 0)
            {
                end = bytes.Length;
            }

            try
            {
                lines.Add(StrictUtf8.GetString(bytes, start, end - start));
            }
            catch (DecoderFallbackException)
            {
                errors.Add($"{fileName}:{lineNumber}: the line is not valid UTF-8; save the file as UTF-8.");
                lines.Add(string.Empty);
            }

            start = next;
            lineNumber++;
        }

        return Build(lines, fileName, errors);
    }

    /// <summary>Parses text that was already decoded.</summary>
    public static ConfDocument ParseText(string text, string fileName)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var lines = new List<string>(text.Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return Build(lines, fileName, []);
    }

    private static ConfDocument Build(List<string> lines, string fileName, List<string> errors)
    {
        var entries = new List<ConfEntry>();
        var firstLine = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < lines.Count; i++)
        {
            int number = i + 1;
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            string? problem = ParseLine(line, out string key, out string path, out string value, out bool quoted);
            if (problem is not null)
            {
                errors.Add($"{fileName}:{number}: {problem}");
                continue;
            }

            if (firstLine.TryGetValue(path, out int earlier))
            {
                errors.Add($"{fileName}:{number}: duplicate key {key} (also set on line {earlier}; keys are case-insensitive and World.Port equals World:Port).");
                continue;
            }

            firstLine[path] = number;
            entries.Add(new ConfEntry(key, path, value, number, quoted));
        }

        if (errors.Count > 0)
        {
            throw new ConfParseException(errors);
        }

        return new ConfDocument(fileName, entries);
    }

    private static string? ParseLine(string line, out string key, out string path, out string value, out bool quoted)
    {
        key = path = value = string.Empty;
        quoted = false;
        if (line[0] == '[')
        {
            return "section headers are not supported; write the full key on each line (for example World.Port = 8085).";
        }

        int equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return "expected 'Key = Value' (no '=' on this line); start a comment with '#'.";
        }

        key = line[..equals].Trim();
        if (key.Length == 0)
        {
            return "the key is empty (nothing before '=').";
        }

        foreach (char c in key)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                return $"the key '{key}' contains whitespace or a quote.";
            }
        }

        path = key.Contains(':', StringComparison.Ordinal) ? key : key.Replace('.', ':');
        foreach (string segment in path.Split(':'))
        {
            if (segment.Length == 0)
            {
                return $"the key '{key}' has an empty segment (a leading, trailing or doubled separator).";
            }
        }

        string rest = line[(equals + 1)..].Trim();
        if (rest.Length == 0 || rest[0] != '"')
        {
            value = rest;
            return null;
        }

        var sb = new StringBuilder();
        int index = 1;
        while (index < rest.Length)
        {
            char c = rest[index];
            if (c == '\\' && index + 1 < rest.Length && (rest[index + 1] == '"' || rest[index + 1] == '\\'))
            {
                sb.Append(rest[index + 1]);
                index += 2;
                continue;
            }

            if (c == '"')
            {
                if (rest[(index + 1)..].Trim().Length > 0)
                {
                    return $"unexpected text after the closing quote of the value of {key}.";
                }

                value = sb.ToString();
                quoted = true;
                return null;
            }

            sb.Append(c);
            index++;
        }

        return $"the quoted value of {key} is not closed (a missing closing quote).";
    }
}

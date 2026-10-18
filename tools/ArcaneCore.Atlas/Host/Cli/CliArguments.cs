using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>The command line is wrong (exit code 2): unknown verb or option, missing value, malformed key=value or script.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>A verb could not do its job although the command line was fine (exit code 1): no running instance, unreadable file.</summary>
internal sealed class CliFailure(string message) : Exception(message);

/// <summary>
/// One verb's options and positional words. <c>--name value</c> and <c>--name=value</c> both work; a bare <c>--</c> ends option
/// parsing. Immutable after <see cref="Parse"/>.
/// </summary>
internal sealed class ParsedArguments
{
    private readonly Dictionary<string, string> _options;

    private ParsedArguments(Dictionary<string, string> options, List<string> positionals)
    {
        _options = options;
        Positionals = positionals;
    }

    /// <summary>The words that are not options, in order (the command, then its <c>key=value</c> pairs).</summary>
    public IReadOnlyList<string> Positionals { get; }

    /// <summary>Whether the option was given.</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>The option's value, or null when it was not given (flags have an empty value).</summary>
    public string? Value(string name) => _options.GetValueOrDefault(name);

    /// <param name="args">Everything after the verb.</param>
    /// <param name="allowed">Option name (with the leading dashes) to whether it takes a value.</param>
    /// <exception cref="UsageException">An option is unknown, repeated, or missing its value.</exception>
    public static ParsedArguments Parse(IReadOnlyList<string> args, IReadOnlyDictionary<string, bool> allowed)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionals = new List<string>();
        bool optionsEnded = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            string name = arg;
            string? inline = null;
            int equals = arg.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }

            if (!allowed.TryGetValue(name, out bool takesValue))
            {
                throw new UsageException($"unknown option '{name}'");
            }

            if (options.ContainsKey(name))
            {
                throw new UsageException($"option '{name}' was given twice");
            }

            if (takesValue)
            {
                if (inline is null)
                {
                    i++;
                    inline = i < args.Count ? args[i] : throw new UsageException($"option '{name}' needs a value");
                }

                options[name] = inline;
            }
            else
            {
                options[name] = inline is null ? string.Empty : throw new UsageException($"option '{name}' does not take a value");
            }
        }

        return new ParsedArguments(options, positionals);
    }
}

/// <summary>Turns <c>key=value</c> words and <c>--json</c> into the argument object of a command.</summary>
internal static class CommandLineArguments
{
    /// <summary>
    /// Values that are valid JSON (numbers, booleans, null, strings, arrays, objects) parse as JSON; anything else is a string,
    /// so <c>at=-9462.66,16.19,57.05</c> stays the text the command's point getter splits.
    /// </summary>
    public static JsonNode? ParseValue(string text)
    {
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, HostJson.Strict);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>The argument object from an optional <c>--json</c> object (or <c>-</c> for standard input) plus <c>key=value</c> pairs.</summary>
    /// <exception cref="UsageException">The JSON is not an object, a pair is malformed, or a key is given twice.</exception>
    public static async Task<JsonObject> BuildAsync(IEnumerable<string> pairs, string? json, TextReader standardInput, CancellationToken cancellationToken)
    {
        JsonObject args = [];
        if (json is not null)
        {
            string text = json == "-" ? await standardInput.ReadToEndAsync(cancellationToken).ConfigureAwait(false) : json;
            try
            {
                args = JsonNode.Parse(text, nodeOptions: null, HostJson.Strict) as JsonObject
                    ?? throw new UsageException("--json must be a JSON object");
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                throw new UsageException($"--json is not valid JSON: {e.Message}");
            }
        }

        foreach (string pair in pairs)
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"'{pair}' is not key=value");
            }

            string key = pair[..equals];
            if (!IsKey(key))
            {
                throw new UsageException($"'{key}' is not a valid argument name (letters, digits, '_', '-', '.')");
            }

            if (args.ContainsKey(key))
            {
                throw new UsageException($"argument '{key}' was given twice");
            }

            args[key] = ParseValue(pair[(equals + 1)..]);
        }

        return args;
    }

    private static bool IsKey(string key)
        => key.Length > 0 && char.IsAsciiLetter(key[0]) && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}

/// <summary>One line of a <c>script</c> file.</summary>
internal sealed record ScriptStep(int Line, string Command, JsonObject Args);

/// <summary>Reads <c>script</c> files: JSONL, one <c>{"command":"...","args":{...}}</c> per line, blank lines ignored.</summary>
internal static class ScriptFile
{
    private const int MaxReportedProblems = 10;

    /// <summary>Parses every line before anything runs, so a typo on line 40 does not leave a half-applied script.</summary>
    /// <exception cref="UsageException">One or more lines are malformed (the message lists them).</exception>
    public static IReadOnlyList<ScriptStep> Parse(string text)
    {
        var steps = new List<ScriptStep>();
        var problems = new List<string>();
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int number = i + 1;
            string? problem = TryParseLine(line, number, out ScriptStep? step);
            if (problem is null)
            {
                steps.Add(step!);
            }
            else if (problems.Count < MaxReportedProblems)
            {
                problems.Add($"line {number}: {problem}");
            }
        }

        if (problems.Count > 0)
        {
            throw new UsageException("the script is malformed: " + string.Join("; ", problems));
        }

        if (steps.Count == 0)
        {
            throw new UsageException("the script has no commands");
        }

        return steps;
    }

    private static string? TryParseLine(string line, int number, out ScriptStep? step)
    {
        step = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line, nodeOptions: null, HostJson.Strict);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return $"not valid JSON ({e.Message})";
        }

        if (node is not JsonObject obj)
        {
            return "expected a JSON object {\"command\":\"...\",\"args\":{...}}";
        }

        foreach ((string key, _) in obj)
        {
            if (key is not ("command" or "args"))
            {
                return $"unknown property '{key}' (expected command and args)";
            }
        }

        if (obj["command"] is not JsonValue commandValue || !commandValue.TryGetValue(out string? command) || string.IsNullOrWhiteSpace(command))
        {
            return "'command' must be a non-empty string";
        }

        JsonObject args;
        switch (obj["args"])
        {
            case null:
                args = [];
                break;
            case JsonObject argsObject:
                args = (JsonObject)argsObject.DeepClone();
                break;
            default:
                return "'args' must be a JSON object";
        }

        step = new ScriptStep(number, command, args);
        return null;
    }
}

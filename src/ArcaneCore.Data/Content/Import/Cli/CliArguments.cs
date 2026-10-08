namespace ArcaneCore.Data.Content.Import;

/// <summary>The command line is wrong (exit code <see cref="ExitCodes.Usage"/>).</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// <c>command [positional…] [--option value | --option=value | --flag]</c>. Each command has a
/// closed set of options; anything else is a usage error rather than being ignored.
/// </summary>
internal sealed class CliArguments
{
    private static readonly string[] s_valueOptions =
        ["--dialect", "--report", "--database", "--provider", "--connection-string", "--dbc-dir", "--quest-xp", "--level-stats-file", "--class-mask-file",
            "--player-stats-migrations-dir", "--cooldown-unit"];

    private static readonly string[] s_flags = ["--replace", "--dry-run", "--verbose", "--migrate"];

    private static readonly Dictionary<string, string[]> s_allowed = new(StringComparer.Ordinal)
    {
        ["plan"] = ["--dialect", "--report", "--verbose"],
        ["import"] = ["--dialect", "--report", "--database", "--provider", "--connection-string", "--dbc-dir", "--quest-xp", "--level-stats-file", "--player-stats-migrations-dir", "--replace", "--dry-run", "--verbose"],
        ["import-dbc"] = ["--database", "--provider", "--connection-string"],
        ["import-map-dbc"] = ["--database", "--provider", "--connection-string", "--replace", "--report"],
        ["verify"] = ["--database", "--provider", "--connection-string"],
        ["class-masks"] = ["--class-mask-file", "--dry-run"],
        ["proc-events"] = ["--database", "--provider", "--connection-string", "--cooldown-unit", "--dry-run"],
        ["refresh"] = ["--database", "--provider", "--connection-string", "--dbc-dir", "--cooldown-unit", "--report", "--dry-run", "--migrate"],
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CliArguments(string command, List<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string Command { get; }

    public IReadOnlyList<string> Positional { get; }

    public static bool IsCommand(string name) => s_allowed.ContainsKey(name);

    public string? Value(string option) => _values.GetValueOrDefault(option);

    public bool Flag(string option) => _flags.Contains(option);

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        string command = args[0];
        if (!s_allowed.TryGetValue(command, out string[]? allowed))
        {
            throw new UsageException($"unknown command '{command}'");
        }

        var positional = new List<string>();
        var result = new CliArguments(command, positional);
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
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

            if (!allowed.Contains(name))
            {
                throw new UsageException(
                    Array.IndexOf(s_valueOptions, name) >= 0 || Array.IndexOf(s_flags, name) >= 0
                        ? $"option {name} does not apply to '{command}'"
                        : $"unknown option '{name}'");
            }

            if (Array.IndexOf(s_flags, name) >= 0)
            {
                if (inline is not null)
                {
                    throw new UsageException($"option {name} takes no value");
                }

                result._flags.Add(name);
                continue;
            }

            if (inline is null)
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException($"option {name} needs a value");
                }

                inline = args[++i];
            }

            result._values[name] = inline;
        }

        return result;
    }
}

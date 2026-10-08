using System.Globalization;
using ArcaneCore.Data.Content.Import;

namespace ArcaneCore.Data.Schema.Upgrade.Cli;

/// <summary>
/// <c>command [--option value | --option=value | --flag]</c>. Each command has a closed set of
/// options; anything else is a usage error rather than being ignored.
/// </summary>
internal sealed class DbUpgradeArguments
{
    internal static readonly string[] ValueOptions = ["--component", "--lock-timeout", "--backup-dir"];

    internal static readonly string[] Flags =
        ["--confirm-backup", "--allow-active-sessions", "--script", "--json", "--no-fail-on-pending", "--apply"];

    internal static readonly string[] Commands = ["status", "plan", "check", "upgrade", "migrate-codex", "backup-info"];

    private static readonly Dictionary<string, string[]> s_allowed = new(StringComparer.Ordinal)
    {
        ["status"] = ["--component", "--json", "--no-fail-on-pending"],
        ["plan"] = ["--component", "--script", "--json", "--no-fail-on-pending"],
        ["check"] = ["--component", "--json"],
        ["upgrade"] = ["--component", "--lock-timeout", "--confirm-backup", "--backup-dir", "--allow-active-sessions"],
        ["migrate-codex"] = ["--component", "--apply", "--lock-timeout", "--confirm-backup", "--backup-dir", "--allow-active-sessions"],
        ["backup-info"] = ["--component"],
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private DbUpgradeArguments(string command) => Command = command;

    public string Command { get; }

    public string? Value(string option) => _values.GetValueOrDefault(option);

    public bool Flag(string option) => _flags.Contains(option);

    /// <summary>The components to act on, in the order they must be upgraded (auth first: the realm needs it).</summary>
    public IReadOnlyList<string> Components()
    {
        string value = Value("--component") ?? "all";
        return value switch
        {
            "all" => ["auth", "characters", "world"],
            "auth" or "characters" or "world" => [value],
            _ => throw new UsageException($"--component must be auth, characters, world or all, not '{value}'"),
        };
    }

    public TimeSpan LockTimeout()
    {
        string? value = Value("--lock-timeout");
        if (value is null)
        {
            return SchemaBootstrapper.DefaultLockTimeout;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > 86400)
        {
            throw new UsageException($"--lock-timeout must be a whole number of seconds between 1 and 86400, not '{value}'");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static DbUpgradeArguments Parse(IReadOnlyList<string> args)
    {
        string command = args[0];
        if (!s_allowed.TryGetValue(command, out string[]? allowed))
        {
            throw new UsageException($"unknown command '{command}'");
        }

        var result = new DbUpgradeArguments(command);
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException($"unexpected argument '{arg}'");
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
                    Array.IndexOf(ValueOptions, name) >= 0 || Array.IndexOf(Flags, name) >= 0
                        ? $"option {name} does not apply to '{command}'"
                        : $"unknown option '{name}'");
            }

            if (Array.IndexOf(Flags, name) >= 0)
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

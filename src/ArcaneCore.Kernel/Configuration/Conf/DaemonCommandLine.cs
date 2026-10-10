using System.Text;

namespace ArcaneCore.Kernel.Configuration.Conf;

public enum DaemonCommandKind
{
    /// <summary>Start the daemon (no arguments, or only recognised options).</summary>
    Run,

    /// <summary>A help token was given: print the usage and exit with <c>ExitCodes.Usage</c>.</summary>
    Help,

    /// <summary>An unknown verb, an unknown or silently-droppable option, or bad verb arguments: print <see cref="DaemonCommand.Message"/> and exit with <c>ExitCodes.Usage</c>.</summary>
    UsageError,

    /// <summary>Run <see cref="DaemonCommand.Verb"/> instead of the daemon.</summary>
    Verb,
}

public enum WriteConfigMode
{
    None,
    Defaults,
    Current,
}

/// <summary>The result of reading a daemon's command line. <see cref="HostArgs"/> are the arguments for the .NET host, without <c>--config</c> and the verb.</summary>
public sealed record DaemonCommand(
    DaemonCommandKind Kind,
    IReadOnlyList<string> HostArgs,
    string? ConfigPath = null,
    string? Verb = null,
    WriteConfigMode WriteMode = WriteConfigMode.None,
    string? OutputPath = null,
    bool Force = false,
    string? Message = null,
    bool IncludeSecrets = false);

/// <summary>
/// Reads the command line of a daemon <em>before</em> the host is built, so that <c>--help</c>, a mistyped verb or a mistyped option
/// cannot start a live server (the .NET command-line provider silently drops <c>--help</c>, single-dash tokens and a trailing
/// <c>--key</c> with no value). Tokens are read exactly as <c>CommandLineConfigurationProvider</c> reads them:
/// <c>--key=value</c>, <c>--key value</c>, <c>/key=value</c>, <c>key=value</c>. Pure; no I/O.
/// </summary>
public static class DaemonCommandLine
{
    public const string CheckConfig = "check-config";
    public const string WriteConfig = "write-config";

    private static readonly string[] Verbs = [CheckConfig, WriteConfig];
    private static readonly string[] HelpTokens = ["--help", "-h", "-?", "/?", "/h"];
    private static readonly string[] HostKeys = ["environment", "contentRoot", "applicationName"];

    public static DaemonCommand Parse(string[] args, ConfigDaemon daemon, ConfCatalog catalog)
    {
        foreach (string arg in args)
        {
            if (HelpTokens.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                return new DaemonCommand(DaemonCommandKind.Help, []);
            }
        }

        string program = ProgramName(daemon);
        int index = 0;
        string? verb = null;
        if (args.Length > 0 && IsBareWord(args[0]))
        {
            verb = Verbs.FirstOrDefault(v => string.Equals(v, args[0], StringComparison.OrdinalIgnoreCase));
            if (verb is null)
            {
                string? near = Verbs.FirstOrDefault(v => ConfCatalog.EditDistance(args[0], v, 3) <= 3);
                return Error($"{program}: unknown command '{args[0]}'." + (near is null ? string.Empty : $" Did you mean '{near}'?") + $" Commands: {string.Join(", ", Verbs)}.");
            }

            index = 1;
        }

        var hostArgs = new List<string>();
        string? configPath = null;
        WriteConfigMode mode = WriteConfigMode.None;
        string? output = null;
        bool force = false;
        bool includeSecrets = false;
        for (; index < args.Length; index++)
        {
            string token = args[index];
            if (verb == WriteConfig && (token.Equals("--defaults", StringComparison.OrdinalIgnoreCase) || token.Equals("--current", StringComparison.OrdinalIgnoreCase)))
            {
                if (mode != WriteConfigMode.None)
                {
                    return Error($"{program}: write-config takes exactly one of --defaults and --current.");
                }

                if (index + 1 >= args.Length)
                {
                    return Error($"{program}: write-config {token} needs a file name (or - for standard output).");
                }

                mode = token.Equals("--defaults", StringComparison.OrdinalIgnoreCase) ? WriteConfigMode.Defaults : WriteConfigMode.Current;
                output = args[++index];
                continue;
            }

            if (verb == WriteConfig && token.Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (verb == WriteConfig && token.Equals("--include-secrets", StringComparison.OrdinalIgnoreCase))
            {
                includeSecrets = true;
                continue;
            }

            if (IsBareWord(token))
            {
                return Error($"{program}: unexpected argument '{token}'. Options look like --Section:Key=value; a command must be the first argument.");
            }

            if (token.StartsWith('-') && !token.StartsWith("--", StringComparison.Ordinal))
            {
                return Error($"{program}: '{token}' is not an option: options start with -- (or /), for example --Section:Key=value. Run with --help for the usage.");
            }

            int start = token.StartsWith("--", StringComparison.Ordinal) ? 2 : token.StartsWith('/') ? 1 : 0;
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            string key;
            string? value = null;
            if (equals >= 0)
            {
                key = token[start..equals];
            }
            else
            {
                key = token[start..];
                if (index + 1 >= args.Length)
                {
                    return Error($"{program}: the option '{token}' has no value, so the host would drop it silently. Write {token}=<value> or {token} <value>.");
                }

                value = args[index + 1];
            }

            if (key.Length == 0)
            {
                return Error($"{program}: '{token}' names no key.");
            }

            if (key.Equals("config", StringComparison.OrdinalIgnoreCase))
            {
                string path = equals >= 0 ? token[(equals + 1)..] : value!;
                if (path.Length == 0)
                {
                    return Error($"{program}: --config needs a file name.");
                }

                configPath = path;
                if (equals < 0)
                {
                    index++;
                }

                continue;
            }

            string? problem = CheckKey(key, daemon, catalog);
            if (problem is not null)
            {
                return Error($"{program}: {problem}");
            }

            hostArgs.Add(token);
            if (equals < 0)
            {
                hostArgs.Add(value!);
                index++;
            }
        }

        if (verb == WriteConfig)
        {
            if (mode == WriteConfigMode.None)
            {
                return Error($"{program}: write-config needs --defaults <file> or --current <file> (- writes to standard output).");
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return Error($"{program}: write-config needs a file name.");
            }

            if (includeSecrets && mode != WriteConfigMode.Current)
            {
                return Error($"{program}: --include-secrets applies only to --current.");
            }

            if (includeSecrets && output == "-")
            {
                return Error($"{program}: --include-secrets refuses standard output (secrets would land in the terminal or a log); name a file.");
            }
        }

        return verb is null
            ? new DaemonCommand(DaemonCommandKind.Run, hostArgs, configPath)
            : new DaemonCommand(DaemonCommandKind.Verb, hostArgs, configPath, verb, mode, output, force, IncludeSecrets: includeSecrets);
    }

    /// <summary>The usage text (standard output of <c>--help</c>).</summary>
    public static string Usage(ConfigDaemon daemon)
    {
        string program = ProgramName(daemon);
        string file = ConfCatalog.FileName(daemon);
        var sb = new StringBuilder();
        sb.Append(program).Append(" - ArcaneCore ").Append(ConfCatalog.DaemonName(daemon)).Append(" daemon\n\n");
        sb.Append("Usage:\n");
        sb.Append("  ").Append(program).Append(" [options]                                            run the daemon\n");
        sb.Append("  ").Append(program).Append(" check-config [options]                               validate the configuration and exit (0 valid, 78 invalid)\n");
        sb.Append("  ").Append(program).Append(" write-config (--defaults|--current) <file|-> [--force] [--include-secrets] [options]\n");
        sb.Append("      write the documented defaults (").Append(file).Append(".dist) or the effective configuration to a file; --force overwrites.\n");
        sb.Append("      --current masks secrets (connection-string passwords, password/token/secret keys) as ").Append(ConfSecrets.Placeholder).Append("; --include-secrets (--current, a file only,\n");
        sb.Append("      never standard output) writes them unmasked\n\n");
        sb.Append("Options:\n");
        sb.Append("  --config <file>             read this .conf file instead of ").Append(file).Append(" (relative paths are from the working directory)\n");
        sb.Append("  --Section:Key=value         set one configuration key, for example --").Append(daemon == ConfigDaemon.Realm ? "Auth:Port=3724" : "World:Port=8085").Append('\n');
        sb.Append("  --environment <name>        the host environment (Development, Production)\n");
        sb.Append("  --contentRoot <dir>         the directory appsettings.json and ").Append(file).Append(" are read from\n");
        sb.Append("  --help, -h, -?, /?          print this text and exit (exit code 64)\n\n");
        sb.Append("Environment:\n");
        sb.Append("  ARCANECORE_CONFIG           a directory that holds world.conf and realm.conf\n\n");
        sb.Append("Configuration order, lowest first: appsettings.json, appsettings.{Environment}.json, ").Append(file).Append(", user secrets (Development), environment variables, command line.\n");
        sb.Append("Documentation: docs/guide/configuration.md (the ").Append(file).Append(" format), docs/reference/configuration.md (every key), docs/reference/exit-codes.md.\n");
        return sb.ToString();
    }

    public static string ProgramName(ConfigDaemon daemon) => daemon == ConfigDaemon.Realm ? "ArcaneCore.Realm" : "ArcaneCore.World";

    private static DaemonCommand Error(string message) => new(DaemonCommandKind.UsageError, [], Message: message);

    private static bool IsBareWord(string token) => token.Length > 0 && token[0] != '-' && token[0] != '/' && !token.Contains('=', StringComparison.Ordinal);

    private static string? CheckKey(string key, ConfigDaemon daemon, ConfCatalog catalog)
    {
        if (HostKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        ConfKeyClass classification = catalog.Classify(key, daemon);
        switch (classification.Status)
        {
            case ConfKeyStatus.Known or ConfKeyStatus.ChildOfCollection or ConfKeyStatus.Framework:
                return null;
            case ConfKeyStatus.OtherDaemon:
                return $"the option '{key}' belongs to the {(daemon == ConfigDaemon.Realm ? "world" : "realm")} daemon, not this one.";
            case ConfKeyStatus.Section:
                return $"the option '{key}' is a section, not a key; name the key below it (see docs/reference/configuration.md).";
            default:
                string? suggestion = catalog.Suggest(key, daemon);
                return $"unknown option '{key}'; the host would ignore it silently." + (suggestion is null ? " No close match." : $" Did you mean {suggestion}?")
                    + " (Command-line keys use ':' between levels, for example --World:Port=8085.)";
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace ArcaneCore.Atlas.Host.Cli;

/// <summary>
/// The verbs of <c>arcane-atlas</c>: <c>serve</c>, <c>ctl</c>, <c>run</c>, <c>script</c>, <c>mcp</c>, <c>layers</c> and <c>help</c>.
/// Exit codes: 0 success, 1 a command (or the environment it needs) failed, 2 usage error. Command output is one JSON document
/// per command on standard output; diagnostics go to standard error.
/// Stateless: every input comes from the <see cref="CliEnvironment"/>, so verbs can run side by side in one process.
/// </summary>
public static class AtlasCli
{
    /// <summary>Exit code: success.</summary>
    public const int ExitOk = 0;

    /// <summary>Exit code: a command failed, or no instance / file / port was available.</summary>
    public const int ExitFailed = 1;

    /// <summary>Exit code: the command line is malformed.</summary>
    public const int ExitUsage = 2;

    /// <summary>The text <c>help</c> prints.</summary>
    public const string Usage = """
        usage: arcane-atlas <verb> [options]

          serve   [--data DIR] [--client DIR] [--port N] [--open]
                  Start the loopback web app on 127.0.0.1 (a random port unless --port) and print its address as one JSON line.
                  --open starts Microsoft Edge (else Google Chrome) as an app window; Ctrl+C stops the server.
          ctl     [--pid N] [--compact] <command> [key=value ...] [--json OBJECT|-]
                  Send one command to a running serve instance (found through its discovery file).
          run     [--data DIR] [--client DIR] [--compact] <command> [key=value ...] [--json OBJECT|-]
                  Run one command headless: no UI and no server; captures use the CPU renderer.
          script  [--attach [--pid N]] [--keep-going] [--data DIR] [--client DIR] <file.jsonl|->
                  Run a command list, one {"command":"...","args":{...}} per line. Headless unless --attach.
                  Stops at the first failed command unless --keep-going. Prints one JSON line per command.
          mcp     [--attach | --headless] [--pid N] [--data DIR] [--client DIR]
                  MCP server on stdio (JSON-RPC 2.0): every command is a tool. Attaches to a running instance when there
                  is one, else runs headless; --attach requires an instance, --headless never attaches.
          layers  List the layer catalog.
          help    Show this text. 'ctl help' / 'run help' list the commands.

        key=value: numbers, booleans and JSON values are parsed as JSON, anything else is a string
        (for example at=-9462.66,16.19,57.05). --json takes an object; '-' reads it from standard input.
        --data and --client default to the ARCANECORE_TEST_TERRAIN_DIR and ARCANECORE_TEST_CLIENT_DIR environment variables.
        State (discovery files, bookmarks, captures) lives in ARCANECORE_ATLAS_STATE_DIR or the per-user application data folder.
        Exit codes: 0 ok, 1 command failed, 2 usage error.
        """;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Runs the verb named by <paramref name="args"/>[0].</summary>
    /// <param name="args">The command line without the program name.</param>
    /// <param name="environment">Streams, state folder and launcher.</param>
    /// <param name="cancellationToken">Cancelled on Ctrl+C or SIGTERM; <c>serve</c> and <c>mcp</c> end normally when it fires.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        try
        {
            if (args.Count == 0)
            {
                throw new UsageException("a verb is required");
            }

            IReadOnlyList<string> rest = [.. args.Skip(1)];
            if (rest.Count == 1 && rest[0] is "-h" or "--help" || args[0] is "-h" or "--help" or "help")
            {
                environment.Out.WriteLine(Usage);
                return ExitOk;
            }

            return args[0] switch
            {
                "serve" => await ServeAsync(rest, environment, cancellationToken).ConfigureAwait(false),
                "ctl" => await CtlAsync(rest, environment, cancellationToken).ConfigureAwait(false),
                "run" => await RunVerbAsync(rest, environment, cancellationToken).ConfigureAwait(false),
                "script" => await ScriptAsync(rest, environment, cancellationToken).ConfigureAwait(false),
                "mcp" => await McpAsync(rest, environment, cancellationToken).ConfigureAwait(false),
                "layers" => Layers(rest, environment),
                _ => throw new UsageException($"unknown verb '{args[0]}'"),
            };
        }
        catch (UsageException e)
        {
            environment.Error.WriteLine($"arcane-atlas: {e.Message}");
            environment.Error.WriteLine("Run 'arcane-atlas help' for usage.");
            return ExitUsage;
        }
        catch (CliFailure e)
        {
            environment.Error.WriteLine($"arcane-atlas: {e.Message}");
            return ExitFailed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitFailed;
        }
    }

    private static int Layers(IReadOnlyList<string> rest, CliEnvironment environment)
    {
        if (rest.Count > 0)
        {
            throw new UsageException("'layers' takes no arguments");
        }

        foreach (LayerInfo layer in LayerCatalog.All)
        {
            environment.Out.WriteLine($"{layer.Id,-20} {layer.Group,-10} {layer.View,-10} {layer.Title}");
        }

        return ExitOk;
    }

    private static async Task<int> ServeAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(args, new Dictionary<string, bool> { ["--data"] = true, ["--client"] = true, ["--port"] = true, ["--open"] = false });
        RejectPositionals(parsed, "serve");
        int port = 0;
        if (parsed.Value("--port") is { } portText
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535))
        {
            throw new UsageException("--port must be a number from 0 to 65535");
        }

        AtlasPaths paths = ResolvePaths(parsed, environment);
        ILoggerFactory? owned = environment.Loggers is null ? CreateConsoleLoggers(LogLevel.Information) : null;
        try
        {
            using AtlasContext context = HostComposer.Compose(paths, environment.Loggers ?? owned!, headless: false, Modules(environment));
            AtlasServer server;
            try
            {
                server = await AtlasServer.StartAsync(context, new AtlasServerOptions { Port = port, Assets = environment.Assets }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
            {
                throw new CliFailure($"cannot listen on 127.0.0.1:{port}: {e.Message}");
            }

            await using (server.ConfigureAwait(false))
            {
                var address = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["port"] = server.Port,
                    ["url"] = server.BaseAddress.AbsoluteUri,
                    ["discovery"] = server.DiscoveryFile,
                    ["version"] = AtlasVersion.Current,
                };
                environment.Out.WriteLine(address.ToJsonString());
                environment.Error.WriteLine($"arcane-atlas: listening on {server.BaseAddress.AbsoluteUri} (pid {Environment.ProcessId}); Ctrl+C stops it.");
                if (parsed.Has("--open"))
                {
                    string? browser = environment.Browser.TryOpenApp(server.BrowserAddress);
                    if (browser is null)
                    {
                        // The user asked for a window and there is none: this is the only place the token is printed.
                        environment.Error.WriteLine($"arcane-atlas: no Microsoft Edge or Google Chrome found; open {server.BrowserAddress.AbsoluteUri} in a browser.");
                    }
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ctrl+C or SIGTERM: leave the scope, which stops the listener and deletes the discovery file.
                }
            }

            return ExitOk;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static async Task<int> CtlAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(args, new Dictionary<string, bool> { ["--pid"] = true, ["--json"] = true, ["--compact"] = false });
        (string command, JsonObject commandArgs) = await CommandLineAsync(parsed, "ctl", environment, cancellationToken).ConfigureAwait(false);
        DiscoveryRecord record = ResolveInstance(parsed, environment, required: true)!;
        using var client = new RemoteCommandClient(record, CommandOrigin.Cli);
        return await ExecuteOneAsync(client, command, commandArgs, parsed.Has("--compact"), environment, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunVerbAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(args, new Dictionary<string, bool> { ["--data"] = true, ["--client"] = true, ["--json"] = true, ["--compact"] = false });
        (string command, JsonObject commandArgs) = await CommandLineAsync(parsed, "run", environment, cancellationToken).ConfigureAwait(false);
        AtlasPaths paths = ResolvePaths(parsed, environment);
        ILoggerFactory? owned = environment.Loggers is null ? CreateConsoleLoggers(LogLevel.Warning) : null;
        try
        {
            using AtlasContext context = HostComposer.Compose(paths, environment.Loggers ?? owned!, headless: true, Modules(environment));
            var client = new LocalCommandClient(context, CommandOrigin.Cli);
            return await ExecuteOneAsync(client, command, commandArgs, parsed.Has("--compact"), environment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static async Task<int> ScriptAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(
            args,
            new Dictionary<string, bool> { ["--attach"] = false, ["--keep-going"] = false, ["--pid"] = true, ["--data"] = true, ["--client"] = true });
        if (parsed.Positionals.Count != 1)
        {
            throw new UsageException("script takes exactly one file (or '-' for standard input)");
        }

        bool attach = parsed.Has("--attach");
        if (!attach && parsed.Has("--pid"))
        {
            throw new UsageException("--pid only applies together with --attach");
        }

        if (attach && (parsed.Has("--data") || parsed.Has("--client")))
        {
            throw new UsageException("--data and --client apply to a headless script, not to --attach");
        }

        string source = parsed.Positionals[0];
        string text;
        try
        {
            text = source == "-"
                ? await environment.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false)
                : await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliFailure($"cannot read script '{source}': {e.Message}");
        }

        IReadOnlyList<ScriptStep> steps = ScriptFile.Parse(text);
        if (attach)
        {
            DiscoveryRecord record = ResolveInstance(parsed, environment, required: true)!;
            using var remote = new RemoteCommandClient(record, CommandOrigin.Script);
            return await RunStepsAsync(remote, steps, parsed.Has("--keep-going"), environment, cancellationToken).ConfigureAwait(false);
        }

        AtlasPaths paths = ResolvePaths(parsed, environment);
        ILoggerFactory? owned = environment.Loggers is null ? CreateConsoleLoggers(LogLevel.Warning) : null;
        try
        {
            using AtlasContext context = HostComposer.Compose(paths, environment.Loggers ?? owned!, headless: true, Modules(environment));
            return await RunStepsAsync(new LocalCommandClient(context, CommandOrigin.Script), steps, parsed.Has("--keep-going"), environment, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static async Task<int> RunStepsAsync(ICommandClient client, IReadOnlyList<ScriptStep> steps, bool keepGoing, CliEnvironment environment, CancellationToken cancellationToken)
    {
        int exit = ExitOk;
        foreach (ScriptStep step in steps)
        {
            JsonObject result = await client.InvokeAsync(step.Command, step.Args, cancellationToken).ConfigureAwait(false);
            result["line"] = step.Line;
            environment.Out.WriteLine(result.ToJsonString());
            if (!IsOk(result))
            {
                exit = ExitFailed;
                if (!keepGoing)
                {
                    break;
                }
            }
        }

        return exit;
    }

    private static async Task<int> McpAsync(IReadOnlyList<string> args, CliEnvironment environment, CancellationToken cancellationToken)
    {
        ParsedArguments parsed = ParsedArguments.Parse(
            args,
            new Dictionary<string, bool> { ["--attach"] = false, ["--headless"] = false, ["--pid"] = true, ["--data"] = true, ["--client"] = true });
        RejectPositionals(parsed, "mcp");
        if (parsed.Has("--attach") && parsed.Has("--headless"))
        {
            throw new UsageException("--attach and --headless exclude each other");
        }

        DiscoveryRecord? record = parsed.Has("--headless") ? null : ResolveInstance(parsed, environment, required: parsed.Has("--attach"));
        if (record is not null)
        {
            if (parsed.Has("--data") || parsed.Has("--client"))
            {
                throw new UsageException("an instance is running (pid " + record.Pid + "): --data and --client only apply with --headless");
            }

            environment.Error.WriteLine($"arcane-atlas mcp: attached to the instance with pid {record.Pid} on port {record.Port}");
            using var remote = new RemoteCommandClient(record, CommandOrigin.Mcp);
            await new McpServer(remote, environment.In, environment.Out, environment.Error).RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitOk;
        }

        if (parsed.Has("--pid"))
        {
            throw new UsageException("--pid selects a running instance and cannot be combined with --headless");
        }

        AtlasPaths paths = ResolvePaths(parsed, environment);
        ILoggerFactory? owned = environment.Loggers is null ? CreateConsoleLoggers(LogLevel.Warning) : null;
        try
        {
            using AtlasContext context = HostComposer.Compose(paths, environment.Loggers ?? owned!, headless: true, Modules(environment));
            environment.Error.WriteLine("arcane-atlas mcp: headless (no running instance attached)");
            await new McpServer(new LocalCommandClient(context, CommandOrigin.Mcp), environment.In, environment.Out, environment.Error).RunAsync(cancellationToken).ConfigureAwait(false);
            return ExitOk;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static void RejectPositionals(ParsedArguments parsed, string verb)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw new UsageException($"'{verb}' takes no arguments (got '{parsed.Positionals[0]}')");
        }
    }

    /// <summary>The command and its arguments from <c>&lt;command&gt; [key=value ...] [--json ...]</c>.</summary>
    private static async Task<(string Command, JsonObject Args)> CommandLineAsync(ParsedArguments parsed, string verb, CliEnvironment environment, CancellationToken cancellationToken)
    {
        if (parsed.Positionals.Count == 0)
        {
            throw new UsageException($"{verb} needs a command, for example '{verb} status' ('{verb} help' lists them)");
        }

        JsonObject args = await CommandLineArguments.BuildAsync(parsed.Positionals.Skip(1), parsed.Value("--json"), environment.In, cancellationToken).ConfigureAwait(false);
        return (parsed.Positionals[0], args);
    }

    private static async Task<int> ExecuteOneAsync(ICommandClient client, string command, JsonObject args, bool compact, CliEnvironment environment, CancellationToken cancellationToken)
    {
        JsonObject result = await client.InvokeAsync(command, args, cancellationToken).ConfigureAwait(false);
        environment.Out.WriteLine(compact ? result.ToJsonString() : result.ToJsonString(Indented));
        return IsOk(result) ? ExitOk : ExitFailed;
    }

    private static bool IsOk(JsonObject result) => result["ok"] is JsonValue ok && ok.TryGetValue(out bool flag) && flag;

    /// <summary>
    /// The running instance a verb talks to. With <c>--pid</c> that instance; otherwise the only live one. No instance is an
    /// error (exit 1) when <paramref name="required"/>, else null; several instances without <c>--pid</c> is a usage error.
    /// </summary>
    private static DiscoveryRecord? ResolveInstance(ParsedArguments parsed, CliEnvironment environment, bool required)
    {
        IReadOnlyList<DiscoveryRecord> live = Discovery.Live(environment.State, environment.IsAlive);
        if (parsed.Value("--pid") is { } pidText)
        {
            if (!int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            {
                throw new UsageException("--pid must be a process id");
            }

            return live.FirstOrDefault(r => r.Pid == pid)
                ?? throw new CliFailure($"no running arcane-atlas serve instance has pid {pid}");
        }

        switch (live.Count)
        {
            case 0:
                return required
                    ? throw new CliFailure($"no running 'arcane-atlas serve' instance found (discovery files are read from {environment.State.InstancesDirectory}); start one with 'arcane-atlas serve'")
                    : null;
            case 1:
                return live[0];
            default:
                throw new UsageException($"{live.Count} instances are running (pids {string.Join(", ", live.Select(r => r.Pid))}); choose one with --pid (mcp: or use --headless)");
        }
    }

    private static AtlasPaths ResolvePaths(ParsedArguments parsed, CliEnvironment environment)
    {
        string? data = parsed.Value("--data") ?? environment.GetEnvironmentVariable(AtlasPaths.TerrainEnvironmentVariable);
        string? client = parsed.Value("--client") ?? environment.GetEnvironmentVariable(AtlasPaths.ClientEnvironmentVariable);
        return new AtlasPaths(FullPath(data), FullPath(client));
    }

    private static string? FullPath(string? path) => string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);

    /// <summary>The modules to compose; a discovered host module is replaced by one bound to this run's state folder.</summary>
    private static IReadOnlyList<IAtlasModule> Modules(CliEnvironment environment)
        => environment.Modules
            ?? [.. AtlasModules.Discover(typeof(AtlasModules).Assembly).Select(m => m is HostModule ? new HostModule(environment.State) : m)];

    private static ILoggerFactory CreateConsoleLoggers(LogLevel minimum)
        => LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimum);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });

            // Standard output carries command results and MCP messages only: every log line goes to standard error.
            builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        });
}

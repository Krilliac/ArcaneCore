using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host.Cli;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// The separate-process public host (<c>arcane-atlas public</c>, design 0.10): its own process with no Local listener, no Local feed connection and
/// no packet-tool or UI commands registered. It connects only to the world's web pipe, which admits only the dedicated low-privilege account, so code
/// running in this process cannot reach Local authority. Operator commands (<c>public.status</c>, <c>public.sessions</c>, <c>public.kick</c>,
/// <c>public.revoke</c>) are read from standard input, one <c>name [JSON arguments]</c> per line, and answered on standard output; when standard
/// input is closed the host keeps running until stopped.
/// </summary>
internal static class PublicProcess
{
    /// <summary>Options of the <c>public</c> verb.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Options = new Dictionary<string, bool>
    {
        ["--config"] = true,
        ["--bind"] = true,
        ["--pid"] = true,
        ["--log-level"] = true,
        ["--log-file"] = true,
    };

    /// <summary>Starts the host and runs until <paramref name="cancellationToken"/> fires. Returns the exit code.</summary>
    public static async Task<int> RunAsync(ParsedArguments parsed, CliEnvironment environment, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var overrides = new List<KeyValuePair<string, string?>> { new("Enabled", "true") };
        if (parsed.Value("--bind") is { } bind)
        {
            overrides.Add(new("Bind", bind));
        }

        if (parsed.Value("--pid") is { } pid)
        {
            overrides.Add(new("Feed:Pid", pid));
        }

        string config = parsed.Value("--config") ?? Path.Combine(environment.State.Root, "atlas.json");
        PublicOptions options;
        try
        {
            options = PublicOptions.Load(config, environment.EnvironmentVariables, overrides);
        }
        catch (InvalidDataException e)
        {
            environment.Error.WriteLine($"arcane-atlas: {e.Message}");
            return AtlasCli.ExitFailed;
        }

        // Only the public module: no Local listener, no live feed, no UI hub, no packet tools.
        using AtlasContext context = HostComposer.Compose(new AtlasPaths(null, null), loggers, headless: true, [new PublicModule()]);
        PublicState state = context.Services.Require<PublicState>();
        PublicServer server;
        try
        {
            server = await PublicServer.StartAsync(
                new PublicStartup
                {
                    Options = options,
                    Loggers = loggers,
                    Mode = PublicProcessMode.Separate,
                    Assets = environment.Assets,
                    State = state,
                    GetEnvironmentVariable = environment.GetEnvironmentVariable,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (PublicStartException e)
        {
            foreach (string error in e.Errors)
            {
                environment.Error.WriteLine($"arcane-atlas public: {error}");
            }

            return AtlasCli.ExitFailed;
        }

        await using (server.ConfigureAwait(false))
        {
            environment.Out.WriteLine(new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["port"] = server.Port,
                ["origin"] = server.Origin?.GetLeftPart(UriPartial.Authority),
                ["mode"] = "separate",
                ["version"] = AtlasVersion.Current,
            }.ToJsonString());
            environment.Error.WriteLine("arcane-atlas public: serving; operator commands are read from standard input; Ctrl+C stops it.");
            try
            {
                await CommandLoopAsync(context, environment, cancellationToken).ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C or SIGTERM.
            }
        }

        return AtlasCli.ExitOk;
    }

    private static async Task CommandLoopAsync(AtlasContext context, CliEnvironment environment, CancellationToken cancellationToken)
    {
        while (await environment.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string name = space < 0 ? line : line[..space];
            JsonObject args = [];
            try
            {
                if (space >= 0)
                {
                    args = JsonNode.Parse(line[(space + 1)..], nodeOptions: null, HostJson.Strict) as JsonObject ?? throw new System.Text.Json.JsonException("arguments must be a JSON object");
                }
            }
            catch (System.Text.Json.JsonException e)
            {
                environment.Out.WriteLine(new JsonObject { ["command"] = name, ["ok"] = false, ["error"] = new JsonObject { ["code"] = CommandException.BadArguments, ["message"] = e.Message } }.ToJsonString());
                continue;
            }

            CommandResult result = await context.Commands.InvokeAsync(name, args, context.Services, CommandOrigin.Cli, headless: false, cancellationToken).ConfigureAwait(false);
            environment.Out.WriteLine(result.ToJson().ToJsonString());
        }
    }
}

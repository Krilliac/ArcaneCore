using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Insight;

/// <summary>
/// <c>insight.watch</c>: re-runs commands on each live frame, at most <see cref="MaxHz"/> times a second per watch. A run goes through the
/// command registry with <see cref="CommandOrigin.Script"/> (the registry's origin enum is frozen), so the host's existing command-log handler
/// publishes the result as a <c>command</c> event and the UI redraws the overlay with no change to the overlay code. A result that is
/// re-published is held to <see cref="InsightArgs.MaxWatchedRows"/> rows and <see cref="InsightArgs.NodeBudget"/> JSON nodes by the watched
/// commands themselves, because the host replaces event data over 20,000 nodes with a placeholder and the overlay would silently vanish.
/// </summary>
internal sealed class WatchService : IDisposable
{
    /// <summary>Highest rate of one watch.</summary>
    public const double MaxHz = 2;

    /// <summary>Most commands in one watch.</summary>
    public const int MaxCommands = 8;

    /// <summary>Most watches at once.</summary>
    public const int MaxWatches = 8;

    /// <summary>The commands that may be watched: the ones that cap their own result.</summary>
    public static readonly IReadOnlyList<string> Watchable = ["live.anomalies", "live.heatmap", "live.highlight"];

    private readonly CommandRegistry _commands;
    private readonly IServiceProvider _services;
    private readonly Func<long> _clock;
    private readonly bool _headless;
    private readonly Lock _gate = new();
    private readonly List<Watch> _watches = [];
    private readonly CancellationTokenSource _stop = new();
    private long _runs;
    private long _throttled;
    private long _skippedBusy;
    private int _nextId;

    public WatchService(CommandRegistry commands, IServiceProvider services, Func<long> clock, bool headless)
    {
        _commands = commands;
        _services = services;
        _clock = clock;
        _headless = headless;
    }

    /// <summary>Watch runs that finished (one run is every command of a watch, once).</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>Frames that arrived while a watch was not due yet (counted per watch).</summary>
    public long Throttled => Interlocked.Read(ref _throttled);

    /// <summary>Frames that arrived while the previous run of a watch was still going (counted per watch).</summary>
    public long SkippedBusy => Interlocked.Read(ref _skippedBusy);

    /// <summary>Watches that exist.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _watches.Count;
            }
        }
    }

    /// <summary>Registers <c>insight.watch</c>.</summary>
    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "insight.watch",
            $"Re-run live commands on every live frame (at most {MaxHz:0} times a second) so their overlays follow the world. start takes commands: each is a command name optionally followed by a JSON object of its arguments, for example 'live.highlight {{\"hpBelow\":30}}'; only {string.Join(", ", Watchable)} can be watched. Watched results are capped (1,500 rows, at most 15,000 JSON nodes) and say so with truncated and total. list shows the watches; stop ends one by id, or all.",
            [
                new CommandArg("action", ArgType.String, "start, stop or list.", Required: true, Choices: ["start", "stop", "list"]),
                new CommandArg("commands", ArgType.StringList, $"For start: 1 to {MaxCommands} commands, each 'name' or 'name {{json arguments}}'."),
                new CommandArg("hz", ArgType.Number, $"For start: runs per second, above 0 and up to {MaxHz:0} (default 1).", Default: JsonValue.Create(1.0)),
                new CommandArg("id", ArgType.String, "For stop: the watch id from start or list. Omit to stop every watch."),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Handle(hub.Watch, call))));
    }

    /// <summary>A live frame arrived: starts every watch that is due and idle.</summary>
    public void OnFrame()
    {
        Watch[] due = [];
        lock (_gate)
        {
            if (_watches.Count == 0)
            {
                return;
            }

            long now = _clock();
            var start = new List<Watch>();
            foreach (Watch watch in _watches)
            {
                if (watch.Busy)
                {
                    Interlocked.Increment(ref _skippedBusy);
                }
                else if (watch.LastStartMs != long.MinValue && now - watch.LastStartMs < watch.PeriodMs)
                {
                    Interlocked.Increment(ref _throttled);
                }
                else
                {
                    watch.Busy = true;
                    watch.LastStartMs = now;
                    start.Add(watch);
                }
            }

            due = [.. start];
        }

        foreach (Watch watch in due)
        {
            _ = Task.Run(() => RunAsync(watch));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate)
        {
            _watches.Clear();
        }
    }

    private static JsonNode Handle(WatchService service, CommandInvocation call)
    {
        return call.GetString("action") switch
        {
            "start" => service.Start(call),
            "stop" => service.Stop(call),
            _ => service.List(),
        };
    }

    private JsonNode Start(CommandInvocation call)
    {
        IReadOnlyList<string> texts = call.Has("commands") ? call.GetStringList("commands") : [];
        if (texts.Count is < 1 or > MaxCommands)
        {
            throw InsightArgs.Bad(call, $"start needs between 1 and {MaxCommands} commands.");
        }

        double hz = call.OptionalNumber("hz") ?? 1;
        if (!double.IsFinite(hz) || hz <= 0 || hz > MaxHz)
        {
            throw InsightArgs.Bad(call, $"argument 'hz' must be above 0 and at most {MaxHz:0}.");
        }

        var parsed = new List<(string Text, string Name, JsonObject Args)>();
        foreach (string text in texts)
        {
            parsed.Add(ParseCommand(call, text));
        }

        Watch watch;
        lock (_gate)
        {
            if (_watches.Count >= MaxWatches)
            {
                throw InsightArgs.Bad(call, $"{MaxWatches} watches are already running; stop some first.");
            }

            watch = new Watch("w" + (++_nextId).ToString(CultureInfo.InvariantCulture), hz, [.. parsed])
            {
                PeriodMs = (long)Math.Ceiling(1000.0 / hz),
            };
            _watches.Add(watch);
        }

        return new JsonObject
        {
            ["id"] = watch.Id,
            ["hz"] = hz,
            ["commands"] = new JsonArray([.. parsed.Select(p => (JsonNode?)p.Text)]),
        };
    }

    private JsonNode Stop(CommandInvocation call)
    {
        string? id = call.OptionalString("id");
        lock (_gate)
        {
            if (id is null)
            {
                int all = _watches.Count;
                _watches.Clear();
                return new JsonObject { ["stopped"] = all };
            }

            int removed = _watches.RemoveAll(w => string.Equals(w.Id, id, StringComparison.Ordinal));
            return removed == 0
                ? throw InsightArgs.Bad(call, $"no watch '{id}'; insight.watch list shows the ones that run.")
                : new JsonObject { ["stopped"] = removed };
        }
    }

    private JsonNode List()
    {
        var rows = new JsonArray();
        lock (_gate)
        {
            foreach (Watch watch in _watches)
            {
                rows.Add(new JsonObject
                {
                    ["id"] = watch.Id,
                    ["hz"] = watch.Hz,
                    ["commands"] = new JsonArray([.. watch.Commands.Select(c => (JsonNode?)c.Text)]),
                });
            }
        }

        return new JsonObject { ["watches"] = rows };
    }

    private static (string Text, string Name, JsonObject Args) ParseCommand(CommandInvocation call, string text)
    {
        string trimmed = text.Trim();
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? trimmed : trimmed[..space];
        string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        if (!Watchable.Contains(name, StringComparer.Ordinal))
        {
            throw InsightArgs.Bad(call, $"'{name}' cannot be watched; the watchable commands are {string.Join(", ", Watchable)}.");
        }

        var args = new JsonObject();
        if (rest.Length > 0)
        {
            try
            {
                args = JsonNode.Parse(rest) as JsonObject ?? throw InsightArgs.Bad(call, $"the arguments of '{name}' must be a JSON object.");
            }
            catch (JsonException e)
            {
                throw InsightArgs.Bad(call, $"the arguments of '{name}' are not valid JSON: {e.Message}");
            }
        }

        return (trimmed, name, args);
    }

    private async Task RunAsync(Watch watch)
    {
        try
        {
            foreach ((_, string name, JsonObject args) in watch.Commands)
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                // Never throws for a failing command: the failure is a result, and the host publishes it like any other.
                await _commands.InvokeAsync(name, (JsonObject)args.DeepClone(), _services, CommandOrigin.Script, _headless, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                watch.Busy = false;
            }

            Interlocked.Increment(ref _runs);
        }
    }

    private sealed class Watch(string id, double hz, IReadOnlyList<(string Text, string Name, JsonObject Args)> commands)
    {
        public string Id { get; } = id;

        public double Hz { get; } = hz;

        public IReadOnlyList<(string Text, string Name, JsonObject Args)> Commands { get; } = commands;

        public long PeriodMs { get; init; }

        public long LastStartMs { get; set; } = long.MinValue;

        public bool Busy { get; set; }
    }
}

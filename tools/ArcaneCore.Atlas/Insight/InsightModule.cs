using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary>
/// Live debugging tools on top of the map feed: entity inspector, anomaly markers and heatmaps, record and replay, highlight
/// filters, watches and the perf report (docs/areas/atlas-insight.md). Discovered by reflection; it needs the live module (order 100) and
/// reports a problem, registering nothing, when that module is not loaded.
/// </summary>
public sealed class InsightModule : IAtlasModule
{
    private readonly Func<long>? _clock;
    private readonly SpellNames? _spells;

    /// <summary>The module with the wall clock and the spell names of the client files, when there are any.</summary>
    public InsightModule()
    {
    }

    /// <summary>The module with an injected unix-millisecond clock and spell names (tests).</summary>
    internal InsightModule(Func<long>? clockMs, SpellNames? spells)
    {
        _clock = clockMs;
        _spells = spells;
    }

    /// <inheritdoc />
    public string Name => "insight";

    /// <inheritdoc />
    public int Order => ModuleOrder.Layers + 10;

    /// <inheritdoc />
    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        LiveFeed? feed = context.Services.Get<LiveFeed>();
        if (feed is null)
        {
            context.ReportProblem("insight: the live module is not loaded, so the live insight commands (live.inspect, live.anomalies, live.heatmap, ...) are not available.");
            return;
        }

        Func<long> clock = _clock ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Lazy<SpellNames?> spells = new(() => _spells ?? LoadSpells(context), LazyThreadSafetyMode.ExecutionAndPublication);
        var hub = new InsightHub(feed, context.Events, context.Commands, context.Services, clock, () => spells.Value, context.Headless);
        context.Services.Add(hub);

        InspectCommands.Register(context.Commands, hub);
        AnomalyCommands.Register(context.Commands, hub);
        HighlightCommand.Register(context.Commands, hub);
        ReplayCommands.Register(context.Commands, hub);
        PerfCommands.Register(context.Commands, hub);
        WatchService.Register(context.Commands, hub);
    }

    private static SpellNames? LoadSpells(AtlasContext context)
    {
        try
        {
            return context.Services.Get<IClientFileSource>() is { } files ? SpellNames.TryLoad(files) : null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            context.ReportProblem($"insight: spell names are not available: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// The module's shared state, registered as a service: the recorders fed from the <c>live.frame</c> topic, the watches, the replay player and
/// the latest UI perf report. Disposing it unsubscribes from the hub and stops the replay and every watch.
/// </summary>
internal sealed class InsightHub : IDisposable
{
    /// <summary>Byte budget of the always-on tail recording the heatmaps read.</summary>
    public const long TailBytes = 16L * 1024 * 1024;

    /// <summary>Age bound of the tail recording.</summary>
    public static readonly TimeSpan TailAge = TimeSpan.FromSeconds(30);

    public const int DefaultRecordMb = 256;
    public const int DefaultRecordMinutes = 15;

    private readonly IDisposable _subscription;
    private readonly Lock _gate = new();
    private InsightRecorder? _record;
    private bool _recording;
    private UiReport? _ui;

    public InsightHub(LiveFeed feed, EventHub events, CommandRegistry commands, IServiceProvider services, Func<long> clock, Func<SpellNames?> spells, bool headless)
    {
        Feed = feed;
        Events = events;
        Clock = clock;
        Spells = spells;
        Services = services;
        Watch = new WatchService(commands, services, clock, headless);
        Replay = new ReplayPlayer(this);
        _subscription = events.Subscribe(OnEvent);
    }

    public LiveFeed Feed { get; }

    public EventHub Events { get; }

    public IServiceProvider Services { get; }

    /// <summary>Unix milliseconds now (injected in tests).</summary>
    public Func<long> Clock { get; }

    /// <summary>The spell names, loaded on first use; null when the client files have none.</summary>
    public Func<SpellNames?> Spells { get; }

    /// <summary>The always-on short recording the heatmaps read.</summary>
    public InsightRecorder Tail { get; } = new(TailBytes, TailAge);

    /// <summary>The user's recording, null until <c>live.record</c> was turned on once.</summary>
    public InsightRecorder? Record
    {
        get
        {
            lock (_gate)
            {
                return _record;
            }
        }
    }

    /// <summary>Whether new frames are added to <see cref="Record"/>.</summary>
    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _recording;
            }
        }
    }

    public WatchService Watch { get; }

    public ReplayPlayer Replay { get; }

    /// <summary>The latest <c>perf.report</c> and when it arrived, or null.</summary>
    public UiReport? LatestUi
    {
        get
        {
            lock (_gate)
            {
                return _ui;
            }
        }
    }

    /// <summary>Turns recording on. Keeps the current recorder (and its frames) unless a size is given that differs from it.</summary>
    public InsightRecorder StartRecording(int? maxMb, int? maxMinutes)
    {
        lock (_gate)
        {
            long bytes = (maxMb ?? (_record is { } current ? (int)(current.MaxBytes / (1024 * 1024)) : DefaultRecordMb)) * 1024L * 1024;
            TimeSpan age = TimeSpan.FromMinutes(maxMinutes ?? (_record is { } existing ? (int)existing.MaxAge.TotalMinutes : DefaultRecordMinutes));
            if (_record is null || _record.MaxBytes != bytes || _record.MaxAge != age)
            {
                _record = new InsightRecorder(bytes, age);
            }

            _recording = true;
            return _record;
        }
    }

    /// <summary>Turns recording off; what was recorded stays for replay.</summary>
    public void StopRecording()
    {
        lock (_gate)
        {
            _recording = false;
        }
    }

    public void SetUi(UiReport report)
    {
        lock (_gate)
        {
            _ui = report;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription.Dispose();
        Watch.Dispose();
        Replay.Dispose();
    }

    private void OnEvent(string topic, JsonNode? data)
    {
        if (topic != EventHub.Topics.LiveFrame)
        {
            return;
        }

        // Cost: every frame is parsed here on the publisher thread into the always-on tail, so the deaths heatmap and live.highlight have history
        // and a current frame without a prior opt-in. It is bounded (TailBytes, TailAge) and eviction is O(1).
        Tail.AddJson(data, out _);
        InsightRecorder? record;
        lock (_gate)
        {
            record = _recording ? _record : null;
        }

        record?.AddJson(data, out _);
        Watch.OnFrame();
    }
}

/// <summary>The latest numbers the UI posted with <c>perf.report</c>.</summary>
internal sealed record UiReport(IReadOnlyDictionary<string, double> Values, long AtMs);

/// <summary>Argument helpers shared by the insight commands.</summary>
internal static class InsightArgs
{
    /// <summary>Hard cap on rows in a result that is re-published as a command event (a watched run); see <see cref="WatchService"/>.</summary>
    public const int MaxWatchedRows = 1500;

    /// <summary>
    /// JSON nodes a watched result may use. The host replaces event data over 20,000 nodes with a placeholder and the overlay then silently
    /// disappears, so results stay at 15,000 (a quarter below it).
    /// </summary>
    public const int NodeBudget = 15_000;

    public static CommandException Bad(CommandInvocation call, string message)
        => new(CommandException.BadArguments, $"{call.Definition.Name}: {message}");

    /// <summary>An optional integer in <paramref name="min"/>..<paramref name="max"/>, else <paramref name="fallback"/>.</summary>
    public static long IntInRange(CommandInvocation call, string name, long min, long max, long fallback)
    {
        long value = call.OptionalInt(name) ?? fallback;
        return value < min || value > max ? throw Bad(call, $"argument '{name}' must be between {min} and {max}.") : value;
    }

    /// <summary>An optional number in <paramref name="min"/>..<paramref name="max"/> (both inclusive), else <paramref name="fallback"/>.</summary>
    public static double NumberInRange(CommandInvocation call, string name, double min, double max, double fallback)
    {
        double value = call.OptionalNumber(name) ?? fallback;
        return !double.IsFinite(value) || value < min || value > max ? throw Bad(call, $"argument '{name}' must be between {min:0.###} and {max:0.###}.") : value;
    }

    /// <summary>An optional non-negative integer that fits a uint.</summary>
    public static uint? OptionalUInt(CommandInvocation call, string name)
    {
        long? value = call.OptionalInt(name);
        return value switch
        {
            null => null,
            >= 0 and <= uint.MaxValue => (uint)value,
            _ => throw Bad(call, $"argument '{name}' must be 0 or more."),
        };
    }

    /// <summary>The required guid argument: 1 to 16 hex digits, not zero.</summary>
    public static ulong RequiredGuid(CommandInvocation call, string name)
    {
        string text;
        try
        {
            text = call.GetString(name);
        }
        catch (CommandException)
        {
            throw Bad(call, $"argument '{name}' must be a guid string in hex; quote it, for example \"1A2B\".");
        }

        return LiveGuid.TryParse(text, out ulong guid) && guid != 0
            ? guid
            : throw Bad(call, $"argument '{name}' must be 1 to 16 hex digits and not zero, for example \"1A2B\".");
    }

    /// <summary>JSON nodes in <paramref name="node"/>, counted the way the host counts them.</summary>
    public static int Nodes(JsonNode? node)
    {
        int count = 1;
        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    count += Nodes(item);
                }

                break;
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    count += Nodes(pair.Value);
                }

                break;
        }

        return count;
    }

    public static JsonArray Point(double x, double y, double z) => new(x, y, z);

    /// <summary>Reads a three-number JSON array.</summary>
    public static bool TryPoint(JsonNode? node, out (double X, double Y, double Z) point)
    {
        point = default;
        if (node is JsonArray { Count: 3 } array
            && array[0] is JsonValue a && a.TryGetValue(out double x)
            && array[1] is JsonValue b && b.TryGetValue(out double y)
            && array[2] is JsonValue c && c.TryGetValue(out double z)
            && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
        {
            point = (x, y, z);
            return true;
        }

        return false;
    }
}

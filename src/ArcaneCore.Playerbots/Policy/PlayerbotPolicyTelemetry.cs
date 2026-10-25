using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>A bot's progress counters at one moment: the stall watch's fingerprint fields the outcome is measured on.</summary>
internal readonly record struct PlayerbotPolicyProgress(uint Level, uint Experience, uint NextLevelExperience, uint Money, uint Items, int QuestsRewarded)
{
    /// <summary>The level with the fraction of it gained (experience over the next level's; a capped level has no fraction).</summary>
    internal double Levels => Level + (NextLevelExperience > 0 ? Math.Min(1.0, (double)Experience / NextLevelExperience) : 0.0);
}

/// <summary>What a decision led to over its outcome window.</summary>
internal readonly record struct PlayerbotPolicyOutcome(int Levels, double LevelProgress, long Experience, long Money, long Items, int Quests, int Stalls, int Deaths,
    long Anomalies);

/// <summary>One telemetry record: a decision and its outcome (<c>docs/areas/playbots.md</c>, Goal policy telemetry).</summary>
internal sealed record PlayerbotPolicyRecord(
    long UnixMs, ulong Bot, uint Map, uint Zone, float[] Features, PlayerbotPolicyGoalMask Open, PlayerbotPolicyGoal? Ladder,
    PlayerbotPolicyGoal? Pick, PlayerbotPolicySource Source, bool Explored, PlayerbotPolicyGoal? Goal, PlayerbotGoalKind Kind,
    uint WindowMs, PlayerbotPolicyOutcome Outcome, PlayerbotPolicyArm Arm = PlayerbotPolicyArm.Policy) : IPlayerbotTelemetryRecord
{
    /// <summary>3: fight records' <c>retreat</c> block adds the <c>losing-burst-N</c> reason and the <c>round_damage</c> feature (version 2 files have neither). 2: the Opportunity goal changed the goal list, so a goal record's <c>open</c> bits (and the model layout) moved; version 1 files have the old seven-goal bits.</summary>
    internal const int SchemaVersion = 3;

    /// <summary>The record as one JSON line (with its newline).</summary>
    public void WriteTo(IBufferWriter<byte> output)
    {
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", SchemaVersion);
            json.WriteString("k", "goal");
            json.WriteNumber("t", UnixMs);
            json.WriteString("bot", Bot.ToString("X", CultureInfo.InvariantCulture));
            json.WriteNumber("map", Map);
            json.WriteNumber("zone", Zone);
            json.WriteStartArray("f");
            foreach (float value in Features) json.WriteNumberValue(MathF.Round(value, 4));
            json.WriteEndArray();
            json.WriteNumber("open", Open.Bits);
            WriteGoal(json, "ladder", Ladder);
            WriteGoal(json, "pick", Pick);
            json.WriteString("src", Source.ToString().ToLowerInvariant());
            json.WriteNumber("x", Explored ? 1 : 0);
            json.WriteString("arm", Arm == PlayerbotPolicyArm.Ladder ? "ladder" : "policy");
            WriteGoal(json, "goal", Goal);
            json.WriteString("kind", Kind.ToString());
            json.WriteNumber("w", WindowMs / 1000);
            json.WriteStartObject("o");
            json.WriteNumber("lvl", Outcome.Levels);
            json.WriteNumber("lp", Math.Round(Outcome.LevelProgress, 4));
            json.WriteNumber("xp", Outcome.Experience);
            json.WriteNumber("money", Outcome.Money);
            json.WriteNumber("items", Outcome.Items);
            json.WriteNumber("quests", Outcome.Quests);
            json.WriteNumber("stalls", Outcome.Stalls);
            json.WriteNumber("deaths", Outcome.Deaths);
            json.WriteNumber("anomalies", Outcome.Anomalies);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        output.Write("\n"u8);
    }

    private static void WriteGoal(Utf8JsonWriter json, string name, PlayerbotPolicyGoal? goal)
    {
        if (goal is { } value) json.WriteString(name, value.ToString());
        else json.WriteNull(name);
    }
}

/// <summary>The world tick's overrun state at one moment: the monitor's count of frames over its budget so far, and that budget.</summary>
internal readonly record struct PlayerbotTickHealth(long Overruns, long BudgetMicros);

/// <summary>
/// One <c>health</c> record: how the world tick fared over the interval that ended at <c>t</c> (unix ms, the clock the goal records use), so
/// a trainer can drop decisions made while the world lagged. <c>ms</c> is the interval's length (it started at <c>t - ms</c>), <c>over</c>
/// the frames over the tick budget within it, <c>budget</c> that budget in ms.
/// </summary>
internal sealed record PlayerbotHealthRecord(long UnixMs, long IntervalMs, long Overruns, long BudgetMs) : IPlayerbotTelemetryRecord
{
    public void WriteTo(IBufferWriter<byte> output)
    {
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PlayerbotPolicyRecord.SchemaVersion);
            json.WriteString("k", "health");
            json.WriteNumber("t", UnixMs);
            json.WriteNumber("ms", IntervalMs);
            json.WriteNumber("over", Overruns);
            json.WriteNumber("budget", BudgetMs);
            json.WriteEndObject();
        }
        output.Write("\n"u8);
    }
}

/// <summary>
/// Decision telemetry (<c>World:Playerbots:Policy:Telemetry</c>, off by default): for each bot decision the policy made (the first think
/// that used a snapshot), the features, the open goals, the ladder's and the policy's choice, whether it explored, the goal the think
/// ended on, and, <see cref="PlayerbotPolicyTelemetryOptions.OutcomeMinutes"/> later, what followed: the stall-watch fingerprint delta
/// (levels, experience, money, items, quests handed in), stalls, deaths and the bot's anomaly reports. The world thread only queues
/// finished records; a writer task formats them and appends to bounded rotating files (<see cref="PlayerbotPolicyTelemetryWriter"/>).
/// A decision whose bot leaves before its window ends is dropped (counted), never written with a partial outcome.
/// </summary>
internal sealed class PlayerbotPolicyTelemetry : IAsyncDisposable
{
    /// <summary>Records queued for the writer; past it a record is dropped (counted).</summary>
    internal const int QueueCapacity = 8192;

    /// <summary>The most open decisions one bot keeps (a window of N minutes at one decision every few seconds).</summary>
    internal const int MaxOpenPerBot = 256;

    /// <summary>The most bots whose anomaly reports are counted.</summary>
    internal const int MaxAnomalySubjects = 4096;

    private readonly PlayerbotPolicyTelemetryOptions _options;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly Channel<IPlayerbotTelemetryRecord> _queue =
        Channel.CreateBounded<IPlayerbotTelemetryRecord>(new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly PlayerbotPolicyTelemetryWriter _writer;
    private readonly Task _writing;
    private readonly Dictionary<ulong, BotState> _bots = []; // world thread
    private readonly ConcurrentDictionary<string, long> _anomalies = new(StringComparer.Ordinal);
    private readonly Action<AnomalyNotice> _onAnomaly;
    private uint _nextSweepMs;
    private bool _healthArmed;
    private long _healthLastUnixMs, _healthLastOverruns;
    private long _queued, _dropped, _unfinished, _written;

    internal PlayerbotPolicyTelemetry(PlayerbotPolicyTelemetryOptions options, string directory, TimeProvider? time = null, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _writer = new PlayerbotPolicyTelemetryWriter(directory, options.MaxFileBytes, options.MaxFiles);
        _onAnomaly = OnAnomaly;
        Combat = new PlayerbotCombatTelemetry(Enqueue, () => _time.GetUtcNow().ToUnixTimeMilliseconds());
        AnomalyLedger.Shared.Reported += _onAnomaly;
        _writing = Task.Run(WriteLoopAsync);
        _logger?.LogInformation("Playerbot goal policy telemetry on: {Directory}, {Files} files of at most {Bytes} bytes, outcome over {Minutes} min",
            _writer.Directory, options.MaxFiles, options.MaxFileBytes, options.OutcomeMinutes);
    }

    internal uint WindowMs => (uint)_options.OutcomeMinutes * 60_000u;

    /// <summary>The combat-decision records (fights and turned-down pulls), written with these.</summary>
    internal PlayerbotCombatTelemetry Combat { get; }

    internal PlayerbotPolicyTelemetryWriter Writer => _writer;

    /// <summary>
    /// Where the world tick's overrun count is read (set by the feature; null: no <c>health</c> records). Called on the world thread at most
    /// every <see cref="HealthIntervalMs"/>; returns null while the tick monitor is not running.
    /// </summary>
    internal Func<PlayerbotTickHealth?>? TickHealth { get; set; }

    /// <summary>World ms between two <c>health</c> records.</summary>
    internal const uint HealthIntervalMs = 10_000;

    internal (long Queued, long Dropped, long Unfinished, long Written) Counts
        => (Interlocked.Read(ref _queued), Interlocked.Read(ref _dropped), Interlocked.Read(ref _unfinished), Interlocked.Read(ref _written));

    /// <summary>Lines the writer refused (an old file held open, a line over the file size, after disposal).</summary>
    internal long Refused
    {
        get { lock (_writer) return _writer.Refused; }
    }

    /// <summary>
    /// After a brain think (world thread): note a death, open a decision when this think made one, and close the decisions whose window
    /// ended. Cheap when nothing is due: a dictionary lookup and two compares.
    /// </summary>
    internal void AfterThink(PlayerbotBrain brain, Player player, uint nowMs)
    {
        try { Combat.AfterThink(brain, player, nowMs); }
        catch (Exception error) when (error is not OutOfMemoryException) { FaultLedger.Shared.Record("playerbot.policy-telemetry", error); }
        ulong bot = player.Guid.Value;
        if (!_bots.TryGetValue(bot, out BotState? state))
        {
            if (_bots.Count >= 65_536) return;
            _bots[bot] = state = new BotState(PlayerbotAnomalies.SubjectOf(player)) { WasAlive = player.IsAlive };
        }
        state.LastSeenMs = nowMs;
        if (state.WasAlive && !player.IsAlive) state.Deaths++;
        state.WasAlive = player.IsAlive;

        PlayerbotPolicyProgress? progress = null;
        if (brain.PolicyAgent is { } fresh && !ReferenceEquals(fresh, state.Agent))
        {
            // A new brain (a replaced one, a driver change back to the brain): the old brain's open decisions measured its counters, not
            // these, so they end unfinished, and this brain's think count starts again.
            if (state.Agent is not null) Interlocked.Add(ref _unfinished, state.Open.Count);
            state.Open.Clear();
            state.Agent = fresh;
            state.LastDecideCount = 0;
        }
        if (brain.PolicyAgent is { } agent && agent.DecideCount != state.LastDecideCount)
        {
            state.LastDecideCount = agent.DecideCount;
            // A snapshot's decision is the first think that had its scores (or its exploration goal): a stale snapshot describes a state
            // long gone, and a think before the scores came back left the ladder in charge only for want of them.
            if (agent.LastVerdict is { Window: { Recorded: false } window } verdict
                && verdict.Source is not (PlayerbotPolicySource.Stale or PlayerbotPolicySource.Missing))
            {
                window.Recorded = true;
                if (state.Open.Count < MaxOpenPerBot)
                {
                    progress = ProgressOf(brain, player);
                    state.Open.Enqueue(new OpenDecision(nowMs, _time.GetUtcNow().ToUnixTimeMilliseconds(), player.MapId, player.ZoneId, window, verdict,
                        brain.Goal, progress.Value, brain.StallCount, state.Deaths, AnomaliesOf(state.Subject), brain.OpportunityDriven, brain.QuestTravelDriven));
                }
                else Interlocked.Increment(ref _dropped);
            }
        }

        while (state.Open.TryPeek(out OpenDecision? open) && unchecked(nowMs - open.AtMs) is var elapsed && elapsed >= WindowMs && elapsed <= int.MaxValue)
        {
            state.Open.Dequeue();
            PlayerbotPolicyProgress now = progress ??= ProgressOf(brain, player);
            var outcome = new PlayerbotPolicyOutcome(
                (int)now.Level - (int)open.Start.Level,
                now.Levels - open.Start.Levels,
                (long)now.Experience - open.Start.Experience,
                (long)now.Money - open.Start.Money,
                (long)now.Items - open.Start.Items,
                now.QuestsRewarded - open.Start.QuestsRewarded,
                Math.Max(0, brain.StallCount - open.Stalls),
                state.Deaths - open.Deaths,
                AnomaliesOf(state.Subject) - open.Anomalies);
            Enqueue(new PlayerbotPolicyRecord(open.UnixMs, player.Guid.Value, open.Map, open.Zone, open.Window.Features, open.Window.Open,
                open.Window.LadderPick, open.Verdict.Pick, open.Verdict.Source, open.Window.Explore is not null,
                PlayerbotPolicySchema.GoalOf(open.Kind, open.Opportunity, open.QuestTravel), open.Kind, WindowMs, outcome, state.Agent?.Arm ?? PlayerbotPolicyArm.Policy));
        }
    }

    /// <summary>
    /// Drop the open decisions of bots not seen for a window and a minute (logged out, stopped, taken by another driver): their outcome
    /// cannot be measured (world thread; at most every ten seconds).
    /// </summary>
    internal void Sweep(uint nowMs)
    {
        if (unchecked((int)(nowMs - _nextSweepMs)) < 0) return;
        _nextSweepMs = unchecked(nowMs + HealthIntervalMs);
        WriteHealth();
        uint gone = WindowMs + 60_000;
        Combat.Sweep(nowMs, Math.Max(gone, PlayerbotCombatTelemetry.DowntimeCapMs + 60_000));
        List<ulong>? remove = null;
        foreach ((ulong bot, BotState state) in _bots)
            if (unchecked(nowMs - state.LastSeenMs) > gone) (remove ??= []).Add(bot);
        if (remove is null) return;
        foreach (ulong bot in remove)
        {
            Interlocked.Add(ref _unfinished, _bots[bot].Open.Count);
            _bots.Remove(bot);
        }
    }

    /// <summary>
    /// World thread, from <see cref="Sweep"/>: queue the overruns counted since the last call as one <c>health</c> record. The first call
    /// only takes the baseline, so a record never covers time before telemetry could watch. Two reads and one small record per interval.
    /// </summary>
    private void WriteHealth()
    {
        if (TickHealth?.Invoke() is not { } health) return;
        long now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (_healthArmed)
        {
            long over = Math.Max(0, health.Overruns - _healthLastOverruns);
            Enqueue(new PlayerbotHealthRecord(now, Math.Max(0, now - _healthLastUnixMs), over, health.BudgetMicros / 1000));
        }
        _healthArmed = true;
        _healthLastUnixMs = now;
        _healthLastOverruns = health.Overruns;
    }

    /// <summary>Queue a finished record for the writer (tests call it directly).</summary>
    internal void Enqueue(IPlayerbotTelemetryRecord record)
    {
        if (_queue.Writer.TryWrite(record)) Interlocked.Increment(ref _queued);
        else Interlocked.Increment(ref _dropped);
    }

    /// <summary>Wait until every record queued so far is written and flushed (tests).</summary>
    internal async Task FlushAsync()
    {
        long deadline = Environment.TickCount64 + 10_000;
        while (Interlocked.Read(ref _written) + Refused < Interlocked.Read(ref _queued) && !_writing.IsCompleted
               && Environment.TickCount64 < deadline)
            await Task.Delay(5).ConfigureAwait(false);
        lock (_writer) _writer.Flush();
    }

    private PlayerbotPolicyProgress ProgressOf(PlayerbotBrain brain, Player player)
    {
        uint items = 0;
        foreach (Item item in player.Inventory.AllItems) items += item.Count;
        int rewarded = 0;
        if (brain.Services.GetService<Npc.QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } state)
            foreach (ArcaneCore.Game.Quests.QuestStatusData status in state.Quests.Statuses.Values)
                if (status.Rewarded) rewarded++;
        return new(player.Level, player.GetUInt32(UpdateFields.PlayerXp), player.GetUInt32(UpdateFields.PlayerNextLevelXp), player.Money, items, rewarded);
    }

    private long AnomaliesOf(string subject) => _anomalies.TryGetValue(subject, out long count) ? count : 0;

    private void OnAnomaly(AnomalyNotice notice)
    {
        if (notice.Subject is not { } subject || !subject.StartsWith("bot ", StringComparison.Ordinal)) return;
        if (_anomalies.Count >= MaxAnomalySubjects && !_anomalies.ContainsKey(subject)) return;
        _anomalies.AddOrUpdate(subject, 1, static (_, count) => count + 1);
    }

    private async Task WriteLoopAsync()
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        bool refusing = false;
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out IPlayerbotTelemetryRecord? record))
                {
                    buffer.ResetWrittenCount();
                    record.WriteTo(buffer);
                    bool wrote;
                    lock (_writer) wrote = _writer.Write(buffer.WrittenSpan);
                    if (wrote) Interlocked.Increment(ref _written);
                    // Refusing is silent in the files, so it is said once each way (an old file held open keeps the bound by refusing).
                    if (wrote == refusing)
                    {
                        refusing = !wrote;
                        if (refusing)
                            _logger?.LogWarning("Playerbot goal policy telemetry is refusing records: the oldest file in {Directory} cannot be deleted (held open or read-only), or a record is over MaxFileBytes",
                                _writer.Directory);
                        else _logger?.LogInformation("Playerbot goal policy telemetry is writing again");
                    }
                }
                lock (_writer) _writer.Flush();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The telemetry is advisory: a full disk or a vanished folder stops it, never the server (the host's state: not filed as a bug).
            FaultLedger.Shared.Record("playerbot.policy-telemetry-io", error);
            _logger?.LogWarning(error, "Playerbot goal policy telemetry stopped ({Type}: {Message})", error.GetType().Name, error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.policy-telemetry", error);
            _logger?.LogWarning(error, "Playerbot goal policy telemetry stopped ({Type}: {Message})", error.GetType().Name, error.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        AnomalyLedger.Shared.Reported -= _onAnomaly;
        foreach (BotState state in _bots.Values) Interlocked.Add(ref _unfinished, state.Open.Count);
        _bots.Clear();
        _queue.Writer.TryComplete();
        try { await _writing.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        lock (_writer) _writer.Dispose();
    }

    private sealed class BotState(string subject)
    {
        public string Subject { get; } = subject;
        public bool WasAlive;
        public int Deaths;
        public uint LastSeenMs;
        public long LastDecideCount;
        public PlayerbotPolicyAgent? Agent;
        public readonly Queue<OpenDecision> Open = new();
    }

    private sealed record OpenDecision(uint AtMs, long UnixMs, uint Map, uint Zone, PlayerbotPolicyWindow Window, PlayerbotPolicyVerdict Verdict,
        PlayerbotGoalKind Kind, PlayerbotPolicyProgress Start, int Stalls, int Deaths, long Anomalies, bool Opportunity, bool QuestTravel);
}

/// <summary>
/// Bounded rotating telemetry files: <c>policy-NNNNNNNN.jsonl</c> in one folder, each at most <c>maxFileBytes</c>, at most
/// <c>maxFiles</c> of them (the oldest is deleted first, before the next is made), so the folder never holds more than their product: when
/// an old file cannot be deleted (a reader holds it), no new file is made and the lines are refused until it can be. Not thread-safe:
/// the caller serialises.
/// </summary>
internal sealed class PlayerbotPolicyTelemetryWriter : IDisposable
{
    private const string Prefix = "policy-";
    private const string Suffix = ".jsonl";
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private FileStream? _current;
    private long _sequence;
    private bool _disposed;

    internal PlayerbotPolicyTelemetryWriter(string directory, long maxFileBytes, int maxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 256L);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        Directory = Path.GetFullPath(directory);
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        System.IO.Directory.CreateDirectory(Directory);
        _sequence = Files().Select(SequenceOf).DefaultIfEmpty(0).Max();
    }

    internal string Directory { get; }

    /// <summary>Lines refused: one alone larger than a file may be, no room for a new file (an old one held open), or after disposal.</summary>
    internal long Refused { get; private set; }

    /// <summary>Append one line (with its newline). False when it was refused (counted in <see cref="Refused"/>).</summary>
    internal bool Write(ReadOnlySpan<byte> line)
    {
        if (_disposed || line.Length > _maxFileBytes || (_current is null || _current.Length + line.Length > _maxFileBytes) && !Rotate())
        {
            Refused++;
            return false;
        }
        _current!.Write(line);
        return true;
    }

    internal void Flush() => _current?.Flush();

    /// <summary>Close the current file and start the next, deleting the oldest first to stay within the bound. False: no room (no file).</summary>
    private bool Rotate()
    {
        _current?.Dispose();
        _current = null;
        string[] files = [.. Files().OrderBy(SequenceOf)];
        int kept = files.Length;
        for (int i = 0; i < files.Length && kept > _maxFiles - 1; i++)
        {
            try
            {
                File.Delete(files[i]);
                kept--;
            }
            // A reader holds it: tried again on the next line. Oldest first, so no newer file goes in its place.
            catch (IOException) { break; }
            catch (UnauthorizedAccessException) { break; }
        }
        if (kept > _maxFiles - 1) return false;
        string path = Path.Combine(Directory, Prefix + (++_sequence).ToString("D8", CultureInfo.InvariantCulture) + Suffix);
        _current = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return true;
    }

    internal IEnumerable<string> Files() => System.IO.Directory.EnumerateFiles(Directory, Prefix + "*" + Suffix).Where(file => SequenceOf(file) > 0);

    private static long SequenceOf(string file)
    {
        string name = Path.GetFileName(file);
        return name.Length > Prefix.Length + Suffix.Length
            && long.TryParse(name.AsSpan(Prefix.Length, name.Length - Prefix.Length - Suffix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long n)
            ? n : 0;
    }

    public void Dispose()
    {
        _disposed = true;
        _current?.Dispose();
        _current = null;
    }
}

using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary><c>live.record</c>, <c>live.replay</c>, <c>live.replay.entities</c> and <c>live.replay.diff</c>.</summary>
internal static class ReplayCommands
{
    public const string FrameTopic = "insight.replay.frame";
    public const string StateTopic = "insight.replay.state";

    /// <summary>What a replayed frame does not show, stated on every replayed frame and in the Insight tab.</summary>
    public const string Note = "replay: positions only. Replayed: positions (interpolated between recorded frames), hp, kind, motion and the display id d. Not replayed: spline paths, routes, targets, animation state.";

    public const int MaxRecordMb = 1024;
    public const int MaxRecordMinutes = 120;
    public const double MaxSpeed = 60;
    public const int DefaultEntityLimit = 100;
    public const int MaxEntityLimit = 5000;
    public const double DefaultMovedYards = 10;

    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "live.record",
            $"Record the live frames into a bounded ring for live.replay (positions, hp, kind, motion and d only: no splines, routes or targets). on:true starts (keeping what was recorded unless a different size is given), on:false stops and keeps the recording, no argument shows the state. Defaults: {InsightHub.DefaultRecordMb} MB and {InsightHub.DefaultRecordMinutes} minutes; the oldest frames go first.",
            [
                new CommandArg("on", ArgType.Boolean, "Start (true) or stop (false) recording."),
                new CommandArg("maxMb", ArgType.Integer, $"Byte budget in MB (1..{MaxRecordMb})."),
                new CommandArg("maxMinutes", ArgType.Integer, $"Age bound in minutes of the recording clock (1..{MaxRecordMinutes})."),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Record(hub, call))));

        registry.Register(new CommandDefinition(
            "live.replay",
            "Show a recorded moment on the live model: at (seconds before the newest recorded frame) or seq pick the frame, play/pause/stop control playback at speed. The frames are published on insight.replay.frame, and the UI ignores live frames while a replay is on. Replays positions, hp, kind, motion flags and d; not splines, routes, targets or animation state.",
            [
                new CommandArg("at", ArgType.Number, "Seconds before the newest recorded frame."),
                new CommandArg("seq", ArgType.Integer, "A recorded frame's sequence number."),
                new CommandArg("action", ArgType.String, "play from the picked frame (or the current position), pause, or stop (back to live).", Choices: ["play", "pause", "stop"]),
                new CommandArg("speed", ArgType.Number, $"Playback speed, above 0 and up to {MaxSpeed:0} (default 1).", Default: JsonValue.Create(1.0)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Replay(hub, call))));

        registry.Register(new CommandDefinition(
            "live.replay.entities",
            "List the entities of a recorded moment (headless: no UI needed). at (seconds before the newest frame) or seq picks it; filters combine.",
            [
                new CommandArg("at", ArgType.Number, "Seconds before the newest recorded frame."),
                new CommandArg("seq", ArgType.Integer, "A recorded frame's sequence number."),
                new CommandArg("kind", ArgType.String, "Only this kind.", Choices: LiveKinds.Names),
                new CommandArg("hpBelow", ArgType.Number, "Only entities with health below this percent."),
                new CommandArg("limit", ArgType.Integer, $"Most entities to return (1..{MaxEntityLimit}, default {DefaultEntityLimit}).", Default: JsonValue.Create(DefaultEntityLimit)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(ReplayEntities(hub, call))));

        registry.Register(new CommandDefinition(
            "live.replay.diff",
            "Compare two recorded frames: who appeared, vanished, died (hp fell to zero) or moved more than a distance.",
            [
                new CommandArg("a", ArgType.Integer, "Sequence number of the earlier frame.", Required: true),
                new CommandArg("b", ArgType.Integer, "Sequence number of the later frame.", Required: true),
                new CommandArg("moved", ArgType.Number, $"Yards an entity must have moved, strictly more than (default {DefaultMovedYards:0}).", Default: JsonValue.Create(DefaultMovedYards)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(ReplayDiff(hub, call))));
    }

    /// <summary>One frame as the UI applies it: the live frame shape, plus the declaration of what a replay carries.</summary>
    public static JsonObject FrameJson(ReplayFrame frame, InsightRecorder recorder)
    {
        var entities = new JsonArray();
        foreach (RecordedEntity e in frame.Entities)
        {
            entities.Add(EntityJson(e, recorder));
        }

        return new JsonObject
        {
            ["op"] = "frame",
            ["seq"] = frame.Seq,
            ["clockMs"] = frame.ClockMs,
            ["map"] = frame.Map,
            ["instance"] = frame.Instance,
            ["entities"] = entities,
            ["replay"] = new JsonObject
            {
                ["fields"] = new JsonArray([.. InsightRecorder.ReplayFields.Select(f => (JsonNode?)f)]),
                ["note"] = Note,
            },
        };
    }

    public static JsonObject EntityJson(RecordedEntity e, InsightRecorder recorder)
    {
        var json = new JsonObject
        {
            ["g"] = LiveGuid.Format(e.Guid),
            ["k"] = LiveKinds.Name((LiveKind)e.Kind),
            ["x"] = Math.Round((double)e.X, 3),
            ["y"] = Math.Round((double)e.Y, 3),
            ["z"] = Math.Round((double)e.Z, 3),
            ["hp"] = (int)e.Hp,
        };
        if (recorder.MotionName(e.Flags) is { } motion)
        {
            json["mt"] = motion;
        }

        if ((e.Flags & InsightRecorder.FlagCombat) != 0)
        {
            json["cb"] = true;
        }

        if (e.D != 0)
        {
            json["d"] = e.D;
        }

        return json;
    }

    private static JsonObject Record(InsightHub hub, CommandInvocation call)
    {
        bool? on = call.OptionalBool("on");
        int? maxMb = call.Has("maxMb") ? (int)InsightArgs.IntInRange(call, "maxMb", 1, MaxRecordMb, 0) : null;
        int? maxMinutes = call.Has("maxMinutes") ? (int)InsightArgs.IntInRange(call, "maxMinutes", 1, MaxRecordMinutes, 0) : null;
        if (on is true)
        {
            hub.StartRecording(maxMb, maxMinutes);
        }
        else if (on is false)
        {
            hub.StopRecording();
        }
        else if (maxMb is not null || maxMinutes is not null)
        {
            throw InsightArgs.Bad(call, "maxMb and maxMinutes only apply together with on:true.");
        }

        return RecordState(hub);
    }

    public static JsonObject RecordState(InsightHub hub)
    {
        InsightRecorder? record = hub.Record;
        return new JsonObject
        {
            ["recording"] = hub.IsRecording,
            ["frames"] = record?.FrameCount ?? 0,
            ["bytes"] = record?.Bytes ?? 0,
            ["maxBytes"] = record?.MaxBytes ?? InsightHub.DefaultRecordMb * 1024L * 1024,
            ["maxMinutes"] = (int)(record?.MaxAge.TotalMinutes ?? InsightHub.DefaultRecordMinutes),
            ["oldestSeq"] = record?.OldestSeq ?? 0,
            ["newestSeq"] = record?.NewestSeq ?? 0,
            ["evicted"] = record?.EvictedFrames ?? 0,
            ["dropped"] = record?.DroppedEntities ?? 0,
        };
    }

    private static JsonObject Replay(InsightHub hub, CommandInvocation call)
    {
        double? at = call.OptionalNumber("at");
        long? seq = call.OptionalInt("seq");
        string? action = call.OptionalString("action");
        double speed = InsightArgs.NumberInRange(call, "speed", double.Epsilon, MaxSpeed, 1);
        if (at is not null && seq is not null)
        {
            throw InsightArgs.Bad(call, "give either at or seq, not both.");
        }

        if (at is < 0 || (at is { } a && !double.IsFinite(a)))
        {
            throw InsightArgs.Bad(call, "argument 'at' must be 0 or more seconds.");
        }

        if (at is null && seq is null && action is null)
        {
            throw InsightArgs.Bad(call, "give at or seq to pick a frame, or an action (play, pause, stop).");
        }

        ReplayPlayer player = hub.Replay;
        if (action == "stop")
        {
            return player.Stop();
        }

        if (action == "pause" && at is null && seq is null)
        {
            return player.Pause();
        }

        InsightRecorder record = hub.Record is { FrameCount: > 0 } recorded
            ? recorded
            : throw new CommandException(CommandException.Unavailable, "nothing is recorded; run live.record with on:true first, then replay.");
        ReplayFrame? frame = null;
        if (at is { } secondsAgo)
        {
            long clock = record.NewestClockMs - (long)Math.Round(secondsAgo * 1000);
            frame = record.AtClock(clock);
            if (frame is null)
            {
                throw new CommandException(CommandException.Failed, $"{secondsAgo:0.###} s ago is outside the recording, which covers {RecordedSeconds(record):0.###} s.");
            }
        }
        else if (seq is { } wanted)
        {
            frame = record.Exact(wanted) ?? throw new CommandException(CommandException.Failed, $"frame {wanted} is not in the recording (it holds {record.OldestSeq} to {record.NewestSeq}).");
        }

        return action == "play" ? player.Play(record, frame, speed) : player.Show(record, frame!);
    }

    internal static double RecordedSeconds(InsightRecorder record)
    {
        ReplayFrame? first = record.Exact(record.OldestSeq);
        return first is null ? 0 : (record.NewestClockMs - first.ClockMs) / 1000.0;
    }

    private static JsonObject ReplayEntities(InsightHub hub, CommandInvocation call)
    {
        double? at = call.OptionalNumber("at");
        long? seq = call.OptionalInt("seq");
        if ((at is null) == (seq is null))
        {
            throw InsightArgs.Bad(call, "give exactly one of at and seq.");
        }

        if (at is < 0 || (at is { } a && !double.IsFinite(a)))
        {
            throw InsightArgs.Bad(call, "argument 'at' must be 0 or more seconds.");
        }

        LiveKind? kind = null;
        if (call.OptionalString("kind") is { } kindText)
        {
            kind = LiveKinds.TryParse(kindText, out LiveKind parsed) ? parsed : throw InsightArgs.Bad(call, $"argument 'kind' must be one of {string.Join(", ", LiveKinds.Names)}.");
        }

        double? hpBelow = call.OptionalNumber("hpBelow");
        int limit = (int)InsightArgs.IntInRange(call, "limit", 1, MaxEntityLimit, DefaultEntityLimit);
        InsightRecorder record = hub.Record is { FrameCount: > 0 } recorded
            ? recorded
            : throw new CommandException(CommandException.Unavailable, "nothing is recorded; run live.record with on:true first.");
        ReplayFrame frame = (at is { } secondsAgo ? record.AtClock(record.NewestClockMs - (long)Math.Round(secondsAgo * 1000)) : record.Exact(seq!.Value))
            ?? throw new CommandException(CommandException.Failed, at is null ? $"frame {seq} is not in the recording." : $"{at:0.###} s ago is outside the recording.");

        RecordedEntity[] matched = [.. frame.Entities.Where(e => (kind is null || e.Kind == (byte)kind) && (hpBelow is null || e.Hp < hpBelow))];
        var rows = new JsonArray();
        foreach (RecordedEntity e in matched.Take(limit))
        {
            rows.Add(EntityJson(e, record));
        }

        return new JsonObject
        {
            ["seq"] = frame.Seq,
            ["clockMs"] = frame.ClockMs,
            ["map"] = frame.Map,
            ["matched"] = matched.Length,
            ["returned"] = rows.Count,
            ["truncated"] = rows.Count < matched.Length,
            ["entities"] = rows,
        };
    }

    private static JsonObject ReplayDiff(InsightHub hub, CommandInvocation call)
    {
        long a = call.GetInt("a");
        long b = call.GetInt("b");
        double moved = InsightArgs.NumberInRange(call, "moved", 0, 100_000, DefaultMovedYards);
        InsightRecorder record = hub.Record is { FrameCount: > 0 } recorded
            ? recorded
            : throw new CommandException(CommandException.Unavailable, "nothing is recorded; run live.record with on:true first.");
        ReplayDiff diff;
        try
        {
            diff = record.Diff(a, b, moved);
        }
        catch (ArgumentException e)
        {
            throw new CommandException(CommandException.Failed, $"{e.Message} The recording holds frames {record.OldestSeq} to {record.NewestSeq}.");
        }

        static JsonArray Guids(IReadOnlyList<ulong> list) => new([.. list.Select(g => (JsonNode?)LiveGuid.Format(g))]);
        return new JsonObject
        {
            ["a"] = a,
            ["b"] = b,
            ["movedYards"] = moved,
            ["appeared"] = Guids(diff.Appeared),
            ["vanished"] = Guids(diff.Vanished),
            ["died"] = Guids(diff.Died),
            ["moved"] = Guids(diff.Moved),
        };
    }
}

/// <summary>Plays a recording back by publishing its frames on <see cref="ReplayCommands.FrameTopic"/>; one replay at a time.</summary>
internal sealed class ReplayPlayer(InsightHub hub) : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _play;
    private bool _active;
    private long _seq;
    private InsightRecorder? _record;

    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Publishes one frame and stays on it.</summary>
    public JsonObject Show(InsightRecorder record, ReplayFrame frame)
    {
        lock (_gate)
        {
            CancelPlayback();
            _active = true;
            _record = record;
            _seq = frame.Seq;
        }

        PublishState(playing: false);
        Publish(record, frame);
        return Result(record, frame, playing: false);
    }

    /// <summary>Publishes <paramref name="start"/> (or the frame after the current position, else the oldest) and keeps publishing the recorded frames at <paramref name="speed"/> times real time.</summary>
    public JsonObject Play(InsightRecorder record, ReplayFrame? start, double speed)
    {
        ReplayFrame first;
        CancellationTokenSource cts;
        lock (_gate)
        {
            first = start ?? (_active && ReferenceEquals(_record, record) ? record.Exact(_seq) : null) ?? record.Exact(record.OldestSeq)!;
            CancelPlayback();
            _active = true;
            _record = record;
            _seq = first.Seq;
            cts = _play = new CancellationTokenSource();
        }

        PublishState(playing: true);
        Publish(record, first);
        _ = Task.Run(() => PlayAsync(record, first, speed, cts));
        return Result(record, first, playing: true);
    }

    public JsonObject Pause()
    {
        long seq;
        lock (_gate)
        {
            CancelPlayback();
            seq = _seq;
        }

        PublishState(playing: false);
        return new JsonObject { ["seq"] = seq, ["playing"] = false, ["replaying"] = Active };
    }

    public JsonObject Stop()
    {
        lock (_gate)
        {
            CancelPlayback();
            _active = false;
        }

        PublishState(playing: false);
        return new JsonObject { ["playing"] = false, ["replaying"] = false };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CancelPlayback();
            _active = false;
        }
    }

    private async Task PlayAsync(InsightRecorder record, ReplayFrame from, double speed, CancellationTokenSource cts)
    {
        try
        {
            long seq = from.Seq;
            long clock = from.ClockMs;
            while (!cts.IsCancellationRequested && record.TryNext(seq, out long nextSeq, out long nextClock))
            {
                double wait = Math.Max(0, (nextClock - clock) / speed);
                if (wait >= 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(wait), cts.Token).ConfigureAwait(false);
                }

                ReplayFrame? frame = record.Exact(nextSeq);
                if (frame is null)
                {
                    break;
                }

                lock (_gate)
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }

                    _seq = nextSeq;
                }

                Publish(record, frame);
                seq = nextSeq;
                clock = nextClock;
            }

            lock (_gate)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                _active = false;
                _play = null;
            }

            PublishState(playing: false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelPlayback()
    {
        _play?.Cancel();
        _play = null;
    }

    private JsonObject Result(InsightRecorder record, ReplayFrame frame, bool playing)
        => new()
        {
            ["seq"] = frame.Seq,
            ["clockMs"] = frame.ClockMs,
            ["map"] = frame.Map,
            ["entities"] = frame.Entities.Count,
            ["playing"] = playing,
            ["replaying"] = true,
            ["range"] = new JsonObject { ["oldestSeq"] = record.OldestSeq, ["newestSeq"] = record.NewestSeq },
            ["replay"] = new JsonObject
            {
                ["fields"] = new JsonArray([.. InsightRecorder.ReplayFields.Select(f => (JsonNode?)f)]),
                ["note"] = ReplayCommands.Note,
            },
        };

    private void Publish(InsightRecorder record, ReplayFrame frame)
        => hub.Events.Publish(ReplayCommands.FrameTopic, ReplayCommands.FrameJson(frame, record));

    private void PublishState(bool playing)
    {
        bool active;
        long seq;
        lock (_gate)
        {
            active = _active;
            seq = _seq;
        }

        hub.Events.Publish(ReplayCommands.StateTopic, new JsonObject { ["replaying"] = active, ["playing"] = playing && active, ["seq"] = seq });
    }
}

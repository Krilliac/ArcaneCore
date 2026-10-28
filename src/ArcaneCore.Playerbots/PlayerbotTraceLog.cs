using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Per-bot ring buffer of recent decision, route, and loop transitions plus a single-line diagnostic snapshot
/// formatter for stall, death-loop, unreachable-goal, and action-fault log lines.
/// </summary>
internal sealed partial class PlayerbotTraceLog
{
    internal const int Capacity = 10;
    private const int MaxRenderedTraceEntries = 8;
    private const int MaxRenderedStackFrames = 5;

    internal readonly record struct Entry(uint AtMs, string Event);

    private readonly Entry[] _entries = new Entry[Capacity];
    private int _head;
    private int _count;

    private bool _hasGoal;
    private PlayerbotGoalKind _lastGoal;
    private uint _lastTarget;
    private uint _lastQuest;

    private uint _lastRefusalQuest;
    private uint _lastRefusalEnder;
    private string? _lastRefusalCode;

    private int _lastHomeTeleports;
    private bool _lastHomeBlocked;

    internal int Count => _count;

    /// <summary>Record a discrete breadcrumb transition into the ring buffer.</summary>
    internal void Record(uint nowMs, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_count > 0)
        {
            int prev = (_head + Capacity - 1) % Capacity;
            if (string.Equals(_entries[prev].Event, text, StringComparison.Ordinal)
                && unchecked(nowMs - _entries[prev].AtMs) < 1_000u)
            {
                return;
            }
        }

        _entries[_head] = new Entry(nowMs, text);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Observe goal/target/quest, turn-in refusal, and home-return transitions without allocating when unchanged.
    /// </summary>
    internal void ObserveState(PlayerbotBrain brain, uint nowMs)
    {
        PlayerbotGoalKind goal = brain.Goal;
        uint target = brain.TargetEntry;
        uint quest = brain.QuestId;
        if (!_hasGoal || goal != _lastGoal || target != _lastTarget || quest != _lastQuest)
        {
            _hasGoal = true;
            _lastGoal = goal;
            _lastTarget = target;
            _lastQuest = quest;
            Record(nowMs, string.Create(CultureInfo.InvariantCulture, $"goal={goal}/{target}/{quest}"));
        }

        if (brain.LastTurnInRefusal is { } refusal)
        {
            if (refusal.QuestId != _lastRefusalQuest
                || refusal.EnderEntry != _lastRefusalEnder
                || !string.Equals(refusal.Code, _lastRefusalCode, StringComparison.Ordinal))
            {
                _lastRefusalQuest = refusal.QuestId;
                _lastRefusalEnder = refusal.EnderEntry;
                _lastRefusalCode = refusal.Code;
                Record(nowMs, string.Create(CultureInfo.InvariantCulture,
                    $"turnin-refused:{refusal.QuestId}:{refusal.EnderEntry}:{refusal.Code}"));
            }
        }

        PlayerbotHomeReturn homeReturn = brain.HomeReturn;
        if (homeReturn.Teleports != _lastHomeTeleports)
        {
            _lastHomeTeleports = homeReturn.Teleports;
            Record(nowMs, string.Create(CultureInfo.InvariantCulture, $"home-teleport:{homeReturn.LastTeleport}"));
        }

        if (homeReturn.WalkBlocked != _lastHomeBlocked)
        {
            _lastHomeBlocked = homeReturn.WalkBlocked;
            if (homeReturn.WalkBlocked)
            {
                Record(nowMs, "home-blocked");
            }
        }
    }

    /// <summary>
    /// Formats a compact, single-line diagnostic snapshot of the bot's current state and recent trace entries.
    /// Called only when a QA-relevant warning/error is about to be logged.
    /// </summary>
    internal string FormatSnapshot(Player player, PlayerbotBrain brain)
    {
        uint nowMs = (player.Session as WorldSession)?.World.NowMs ?? 0;
        // The host marks the probe "stall log" (and "settle") after the think, before it takes this snapshot: that names its own bookkeeping,
        // not where the think's work stopped. Mid-think snapshots (exception diagnostics) keep the probe's phase.
        string? probePhase = PlayerbotThinkProbe.CurrentPhase;
        string phase = SanitizeToken(probePhase is null or "stall log" or "settle" ? brain.StalledWorkPhase ?? brain.LastThinkPhase : probePhase);
        uint mapId = player.Map?.MapId ?? player.MapId;
        MovementFlags moveFlags = player.Movement.Flags;

        PlayerbotMotion.MotionState motion = PlayerbotMotion.Of(player);
        PlayerbotRoute? route = motion.Route;
        bool moving = route is { Complete: false } || (moveFlags & MovementFlags.MaskMoving) != 0;
        int wpCount = route?.Points.Count ?? 0;
        int wpIdx = route is { } activeRoute && wpCount > 0 ? Math.Clamp(activeRoute.NextPoint, 0, wpCount) : 0;
        string dest = route is { Points: { Count: > 0 } points }
            ? FormatPos(points[^1])
            : "none";

        string homeSummary;
        if (brain.Home is not null)
        {
            PlayerbotHomeReturn hr = brain.HomeReturn;
            homeSummary = string.Create(CultureInfo.InvariantCulture,
                $"{hr.State.ToString().ToLowerInvariant()}(dist={hr.Distance:F0},walkBlocked={(hr.WalkBlocked ? "yes" : "no")},teleports={hr.Teleports})");
        }
        else
        {
            homeSummary = "none";
        }

        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture,
            $"phase={phase} pos=({player.X:F1},{player.Y:F1},{player.Z:F1}) map={mapId} zone={player.ZoneId} moveFlags={moveFlags} ");
        sb.Append(CultureInfo.InvariantCulture,
            $"motion=moving:{(moving ? "yes" : "no")},wp:{wpIdx}/{wpCount},dest:{dest},loops:{motion.LoopCount} ");
        sb.Append(CultureInfo.InvariantCulture, $"home={homeSummary} trace=[");

        int toRender = Math.Min(_count, MaxRenderedTraceEntries);
        int startOffset = _count - toRender;
        for (int i = 0; i < toRender; i++)
        {
            if (i > 0) sb.Append(" -> ");
            int idx = (_head + Capacity - _count + startOffset + i) % Capacity;
            Entry entry = _entries[idx];
            uint ageSec = nowMs >= entry.AtMs ? (nowMs - entry.AtMs) / 1000u : 0u;
            sb.Append(CultureInfo.InvariantCulture, $"-{ageSec}s:{SanitizeInline(entry.Event)}");
        }

        if (toRender == 0) sb.Append("none");
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Formats a single-line diagnostic summary for an unhandled bot exception, combining the bot's snapshot
    /// with the top <c>ArcaneCore.*</c> stack frames.
    /// </summary>
    internal static string FormatExceptionDiag(Exception ex, Player? player, PlayerbotBrain? brain)
    {
        string prefix = player is not null && brain is not null
            ? brain.FormatDiagnosticSnapshot(player)
            : string.Create(CultureInfo.InvariantCulture,
                $"phase={SanitizeToken(PlayerbotThinkProbe.CurrentPhase ?? brain?.LastThinkPhase ?? "none")}");
        string stack = FormatCondensedStack(ex);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix} stack=[{stack}]");
    }

    internal static string FormatCondensedStack(Exception ex)
    {
        var trace = new StackTrace(ex, fNeedFileInfo: true);
        StackFrame[]? frames = trace.GetFrames();
        if (frames is null || frames.Length == 0) return "none";

        var sb = new StringBuilder(160);
        int added = 0;
        for (int i = 0; i < frames.Length && added < MaxRenderedStackFrames; i++)
        {
            StackFrame frame = frames[i];
            if (frame.GetMethod() is not { DeclaringType: { FullName: { } fullName } declaringType } method
                || !fullName.StartsWith("ArcaneCore", StringComparison.Ordinal))
                continue;

            if (added > 0) sb.Append(" <- ");
            AppendFrame(sb, frame, declaringType.Name, method.Name);
            added++;
        }

        if (added == 0)
        {
            for (int i = 0; i < frames.Length && added < 3; i++)
            {
                StackFrame frame = frames[i];
                var method = frame.GetMethod();
                if (method is null) continue;
                if (added > 0) sb.Append(" <- ");
                AppendFrame(sb, frame, method.DeclaringType?.Name ?? "Unknown", method.Name);
                added++;
            }
        }

        return added == 0 ? "none" : sb.ToString();
    }

    private static void AppendFrame(StringBuilder sb, StackFrame frame, string typeName, string methodName)
    {
        sb.Append("at ").Append(typeName).Append('.').Append(methodName);
        string? file = frame.GetFileName();
        int line = frame.GetFileLineNumber();
        if (!string.IsNullOrEmpty(file) && line > 0)
        {
            sb.Append('(').Append(Path.GetFileName(file)).Append(':').Append(line.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
    }

    private static string FormatPos(Vector3 v)
        => string.Create(CultureInfo.InvariantCulture, $"({v.X:F1},{v.Y:F1},{v.Z:F1})");

    private static string SanitizeToken(string? value)
        => string.IsNullOrWhiteSpace(value) ? "none" : SanitizeInline(value).Replace(' ', '-');

    private static string SanitizeInline(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}

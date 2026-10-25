using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>
/// <c>.debug scheduler</c> (the world tick scheduler and every queue of deferred work: posted world commands, pending teleports, and per map
/// the delayed unit actions, charm links, AI relocation notices, assist calls, relay/db script steps and script despawns),
/// <c>.debug opcodes</c> (per-opcode packet and byte counters of the world and logon protocols, counted locally on demand), and
/// <c>.debug botthink</c> (managed playerbot think diagnostics, feature tick cost, slow-think anomalies, and active goals). No vmangos
/// equivalent; none needs a character, so all work from the console.
/// </summary>
public sealed class DebugQueueCommands : ICommandExtension
{
    /// <summary>Default rows of the list output.</summary>
    public const int DefaultRows = 15;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("scheduler", AccountSecurity.GameMaster,
            "Syntax: .debug scheduler [#count]\nShow the world tick scheduler (interval, late tolerance, late and skipped ticks, next due) and the deferred work queues: posted world commands, pending teleports, and the maps with the most queued work (delayed unit actions, charm links, AI relocation notices, assist calls, relay and db script steps, arrival relays, script despawns; default 15 maps).",
            Scheduler, RetailLevel: 3),
        new("opcodes", AccountSecurity.GameMaster,
            "Syntax: .debug opcodes [on|off|reset|in|out|bytes] [#count]\nPer-opcode traffic since the last reset: packets and bytes in and out by protocol and opcode, busiest first (by packets in+out, 'in', 'out' or 'bytes'; default 15 rows). Counting runs while a metrics listener is attached or after '.debug opcodes on' (off again with 'off'); 'reset' starts a new window.",
            Opcodes, RetailLevel: 3),
        new("botthink", AccountSecurity.GameMaster,
            "Syntax: .debug botthink [#count]\nShow playerbot think diagnostics: logged-in and running counts, split-brain latch, playerbot feature tick time, slow-think anomaly count and recent slowest phases, active goal distribution, and up to #count bots (default 10, max 60).",
            BotThink, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Scheduler(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptInt32(out int count, DefaultRows) || !args.IsEmpty || count < 1) return false;
        count = Math.Min(count, 60);
        WorldRuntime world = context.World;
        WorldTickScheduler scheduler = world.Scheduler;
        int teleports = context.Session.Services.GetService<TeleportFeature>()?.TeleportsIfAttached?.PendingCount ?? 0;
        context.Reply(string.Create(C,
            $"World tick scheduler: interval {scheduler.IntervalMs} ms, late tolerance {scheduler.LateToleranceMs} ms, {scheduler.LateTicks} late and {scheduler.SkippedTicks} skipped ticks, next due {(scheduler.NextDueMs is { } due ? due.ToString(C) : "-")}"));
        context.Reply(string.Create(C, $"  posted world commands waiting: {world.PendingCommandCount}; pending teleports: {teleports}"));
        CreatureWorldFeature? creatures = context.Session.Services.GetService<CreatureWorldFeature>();
        var rows = new List<(Map Map, int Total, string Line)>();
        foreach (Map map in world.Maps)
        {
            MapUnitControl? control = map.FindUpdater<MapUnitControl>();
            CreaturePendingWork work = creatures?.FindSystem(map)?.PendingWork ?? default;
            int unitActions = control?.PendingCount ?? 0, links = control?.LinkCount ?? 0;
            int total = unitActions + work.AiRelocations + work.Assists + work.RelayScriptSteps + work.DbScriptSteps + work.ArrivalRelays + work.ScriptDespawns;
            rows.Add((map, total, string.Create(C,
                $"  map {map.MapId}/{map.InstanceId}: {total} queued: unit actions {unitActions}, relocations {work.AiRelocations}, assists {work.Assists}, relay steps {work.RelayScriptSteps}, db script steps {work.DbScriptSteps}, arrival relays {work.ArrivalRelays}, script despawns {work.ScriptDespawns}; charm links {links}")));
        }

        context.Reply(string.Create(C, $"  {rows.Count} map(s), {rows.Sum(r => r.Total)} queued work items together:"));
        foreach ((Map _, int _, string line) in rows.OrderByDescending(r => r.Total).ThenBy(r => r.Map.MapId).ThenBy(r => r.Map.InstanceId).Take(count))
        {
            context.Reply(line);
        }

        return true;
    }

    private sealed record Baseline(IReadOnlyDictionary<(string Protocol, int Slot), OpcodeCounts> Counts, long At);

    /// <summary>
    /// The start of the <c>.debug opcodes</c> window: a snapshot never changed after it is built, replaced whole by 'on' and 'reset'.
    /// Commands run on the world thread (console, RA) and, while maps update in parallel, on map threads (a player's chat in the
    /// Packets stage), so the reference is volatile; two resets racing keep either window.
    /// </summary>
    private static volatile Baseline? _baseline;

    private static Dictionary<(string, int), OpcodeCounts> Snapshot()
    {
        var counts = new Dictionary<(string, int), OpcodeCounts>();
        foreach (OpcodeTable table in ArcaneMeters.Opcodes.Tables)
        {
            for (int slot = 0; slot < table.SlotCount; slot++)
            {
                counts[(table.Protocol, slot)] = table.ReadSlot(slot);
            }
        }

        return counts;
    }

    private static bool Opcodes(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string order = "all";
        int count = DefaultRows;
        OpcodeTrafficMeter meter = ArcaneMeters.Opcodes;
        while (args.ExtractArg() is { } word)
        {
            switch (word.ToLowerInvariant())
            {
                case "on":
                    meter.LocalCounting = true;
                    _baseline = new Baseline(Snapshot(), Stopwatch.GetTimestamp());
                    context.Reply("Per-opcode counting on; window reset.");
                    return args.IsEmpty;
                case "off":
                    meter.LocalCounting = false;
                    context.Reply(meter.Counting ? "Local counting off (a metrics listener keeps counting)." : "Per-opcode counting off.");
                    return args.IsEmpty;
                case "reset":
                    _baseline = new Baseline(Snapshot(), Stopwatch.GetTimestamp());
                    context.Reply("Per-opcode window reset.");
                    return args.IsEmpty;
                case "in" or "out" or "bytes":
                    order = word.ToLowerInvariant();
                    break;
                default:
                    if (!int.TryParse(word, NumberStyles.None, C, out count) || count < 1) return false;
                    count = Math.Min(count, 60);
                    break;
            }
        }

        Baseline? baseline = _baseline;
        Dictionary<(string Protocol, int Slot), OpcodeCounts> now = Snapshot();
        double seconds = baseline is null ? 0 : Stopwatch.GetElapsedTime(baseline.At).TotalSeconds;
        var rows = new List<(string Label, OpcodeCounts D)>();
        foreach (OpcodeTable table in meter.Tables)
        {
            for (int slot = 0; slot < table.SlotCount; slot++)
            {
                OpcodeCounts n = now[(table.Protocol, slot)];
                OpcodeCounts b = baseline?.Counts.GetValueOrDefault((table.Protocol, slot)) ?? default;
                var d = new OpcodeCounts(n.PacketsIn - b.PacketsIn, n.BytesIn - b.BytesIn, n.PacketsOut - b.PacketsOut, n.BytesOut - b.BytesOut);
                if (d.PacketsIn != 0 || d.PacketsOut != 0) rows.Add((table.Protocol + " " + table.LabelAt(slot), d));
            }
        }

        Func<(string Label, OpcodeCounts D), long> key = order switch
        {
            "in" => r => r.D.PacketsIn,
            "out" => r => r.D.PacketsOut,
            "bytes" => r => r.D.BytesIn + r.D.BytesOut,
            _ => r => r.D.PacketsIn + r.D.PacketsOut,
        };
        string window = baseline is null ? "since start" : string.Create(C, $"over the last {seconds:F0} s");
        context.Reply(string.Create(C,
            $"Per-opcode traffic {window} (counting {(meter.Counting ? meter.LocalCounting ? "on" : "on, metrics listener" : "OFF: use '.debug opcodes on'")}): {rows.Count} opcode(s), {rows.Sum(r => r.D.PacketsIn)} packets in, {rows.Sum(r => r.D.PacketsOut)} out"));
        foreach ((string label, OpcodeCounts d) in rows.OrderByDescending(key).ThenBy(r => r.Label, StringComparer.Ordinal).Take(count))
        {
            string rate = seconds >= 1 ? string.Create(C, $" ({(d.PacketsIn + d.PacketsOut) / seconds:F1}/s)") : string.Empty;
            context.Reply(string.Create(C, $"  {label}: in {d.PacketsIn} pkts {d.BytesIn} B, out {d.PacketsOut} pkts {d.BytesOut} B{rate}"));
        }

        return true;
    }

    private static bool BotThink(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptInt32(out int count, 10) || !args.IsEmpty || count < 1) return false;
        count = Math.Min(count, 60);

        ManagedBotRegistry? registry = context.Session.Services.GetService<ManagedBotRegistry>();
        IPlayerbotService? service = context.Session.Services.GetService<IPlayerbotService>();
        IReadOnlyList<PlayerbotStatus> snapshot = service?.Snapshot() ?? [];
        int loggedIn = registry?.Count ?? 0;
        int running = registry?.RunningCount ?? snapshot.Count(b => b.State == ManagedPlayerbotState.Running);
        bool splitBrain = registry?.SplitBrain ?? false;

        var stats = context.World.Stats.Snapshot();
        var botFeatures = stats.FeatureMeans
            .Where(f => f.Name.Contains("bot", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.MeanMicros)
            .Select(f => string.Create(C, $"{f.Name}={f.MeanMicros / 1000.0:F2} ms"))
            .ToList();
        string featureCost = botFeatures.Count > 0 ? string.Join(", ", botFeatures) : "none";

        AnomalySnapshot? slowThink = AnomalyLedger.Shared.Find("bot-brain/think-slow");
        long slowCount = slowThink?.Count ?? 0;
        string slowSubjects = slowThink?.SubjectsCountText ?? "0";
        AnomalySnapshot? gcStall = AnomalyLedger.Shared.Find("bot-brain/think-gc-stall");

        context.Reply(string.Create(C,
            $"Playerbot think: {running} running ({loggedIn} logged in, {snapshot.Count} registered), split-brain {(splitBrain ? "YES" : "no")}, feature tick {featureCost}"));
        context.Reply(string.Create(C,
            $"  slow thinks (bot-brain/think-slow): {slowCount} total across {slowSubjects} bot(s)"));
        if (gcStall is not null)
            context.Reply(string.Create(C,
                $"  thinks over the threshold only for GC pauses (bot-brain/think-gc-stall, Info): {gcStall.Count} total across {gcStall.SubjectsCountText} bot(s)"));
        if (slowThink is { RecentDetails.Count: > 0 })
        {
            foreach (string detail in slowThink.RecentDetails)
            {
                context.Reply("    slow: " + detail);
            }
        }

        if (snapshot.Count > 0)
        {
            string goals = string.Join(", ", snapshot
                .GroupBy(b => b.Goal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .Select(g => string.Create(C, $"{g.Key}={g.Count()}")));
            int faulted = snapshot.Count(b => b.State == ManagedPlayerbotState.Faulted || !string.IsNullOrEmpty(b.ErrorCode));
            context.Reply(string.Create(C, $"  goals: {goals}; faulted/errors: {faulted}"));
            foreach (PlayerbotStatus bot in snapshot
                .OrderByDescending(b => b.State == ManagedPlayerbotState.Running)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .Take(count))
            {
                string err = !string.IsNullOrEmpty(bot.ErrorCode) ? $" error={bot.ErrorCode}" : string.Empty;
                string risk = !string.IsNullOrEmpty(bot.Risk) ? $" risk={bot.Risk}" : string.Empty;
                context.Reply(string.Create(C,
                    $"  {bot.Name} ({bot.BotId}): {bot.State} goal={bot.Goal} map={bot.MapId} target={bot.TargetEntry} quest={bot.QuestId} hp={bot.Health}{err}{risk}"));
            }
        }
        else if (registry is { Count: > 0 })
        {
            foreach (ManagedBot bot in registry.Bots.Take(count))
            {
                ManagedPlayerbot r = bot.Record;
                context.Reply(string.Create(C,
                    $"  {(bot.Name.Length > 0 ? bot.Name : bot.BotId.ToString())} ({bot.BotId}): {r.State} running={bot.Running} goal={r.Goal} target={r.TargetEntry} quest={r.QuestId}"));
            }
        }

        return true;
    }
}

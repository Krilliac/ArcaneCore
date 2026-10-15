using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Chaos;

/// <summary>
/// <c>.playerbot chaos</c> (GameMaster / Administrator): inspect and control the adversarial server-breaker chaos bot engine
/// (<see cref="PlayerbotChaosEngine"/>; docs/areas/playbots.md). Added to the <c>playerbot</c> root as an <see cref="ICommandExtension"/>.
/// </summary>
public sealed class PlayerbotChaosCommands : ICommandExtension
{
    public const int DefaultBurstProbes = 8;

    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("chaos", AccountSecurity.GameMaster,
            "Syntax: .playerbot chaos [status|on|off|burst [#probes] [$bot]|assign $bot|unassign $bot|faults|clear]\nInspect or control adversarial chaos bots that fuzz handlers, state machines and invariants on the live world.",
            Root,
            Children:
            [
                new ChatCommand("status", AccountSecurity.GameMaster,
                    "Syntax: .playerbot chaos status\nShow chaos engine state, probe counters by category, invariant repairs, assigned bots and recent faults.",
                    Status),
                new ChatCommand("on", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos on [all|#activeBots] [#probesPerThink]\nEnable chaos bot probes immediately on the running world (optionally setting ActiveChaosBots to 'all' or a count, and ProbesPerThink).",
                    On),
                new ChatCommand("off", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos off\nDisable chaos bot probes immediately.",
                    Off),
                new ChatCommand("burst", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos burst [#probes] [$bot]\nExecute an immediate burst of adversarial chaos probes (default 8, 1..256) on all active chaos bots or one named bot.",
                    Burst),
                new ChatCommand("assign", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos assign $id|$name\nDesignate a running or registered bot as an explicit chaos bot.",
                    Assign),
                new ChatCommand("unassign", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos unassign $id|$name\nRemove a bot's explicit chaos assignment.",
                    Unassign),
                new ChatCommand("faults", AccountSecurity.GameMaster,
                    "Syntax: .playerbot chaos faults\nList the most recent unhandled handler faults or invariant violations caught by chaos probes.",
                    Faults),
                new ChatCommand("clear", AccountSecurity.Administrator,
                    "Syntax: .playerbot chaos clear [all]\nReset chaos probe counters and recent fault history ('all' also clears explicit bot assignments).",
                    Clear),
            ]),
    ];

    private static ManagedPlayerbotFeature? FeatureOf(CommandContext context)
    {
        ManagedPlayerbotFeature? feature = context.Session.Services.GetService<ManagedPlayerbotFeature>();
        if (feature is null)
        {
            context.Reply("Playerbots are unavailable.");
            return null;
        }
        return feature;
    }

    private static bool Root(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        return Status(context, string.Empty);
    }

    private static bool Status(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (FeatureOf(context) is not { } feature) return true;
        foreach (string line in FormatStatus(feature.Chaos.Status(), feature.RunningCount))
            context.Reply(line);
        return true;
    }

    private static bool On(CommandContext context, string args)
    {
        if (FeatureOf(context) is not { } feature) return true;
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 2) return false;

        int activeBots = feature.BotOptions.Chaos.ActiveChaosBots;
        int probesPerThink = feature.BotOptions.Chaos.ProbesPerThink;
        if (parts.Length >= 1)
        {
            if (string.Equals(parts[0], "all", StringComparison.OrdinalIgnoreCase))
            {
                activeBots = PlayerbotChaosOptions.MaxActiveChaosBots;
            }
            else if (!int.TryParse(parts[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out activeBots)
                || activeBots is < 0 or > PlayerbotChaosOptions.MaxActiveChaosBots)
            {
                return false;
            }
        }
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[1].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out probesPerThink)
                || probesPerThink is < 1 or > PlayerbotChaosOptions.MaxProbesPerThink)
                return false;
        }

        feature.BotOptions.Chaos.ActiveChaosBots = activeBots;
        feature.BotOptions.Chaos.ProbesPerThink = probesPerThink;
        feature.BotOptions.Chaos.Enabled = true;
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Playerbot chaos enabled: activeBots={activeBots}, probesPerThink={probesPerThink}, assigned={feature.Chaos.Status().AssignedBots}."));
        return true;
    }

    private static bool Off(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (FeatureOf(context) is not { } feature) return true;
        feature.BotOptions.Chaos.Enabled = false;
        context.Reply("Playerbot chaos disabled.");
        return true;
    }

    private static bool Burst(CommandContext context, string args)
    {
        if (FeatureOf(context) is not { } feature) return true;
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 2) return false;

        int probes = DefaultBurstProbes;
        string? targetBot = null;
        if (parts.Length >= 1)
        {
            if (int.TryParse(parts[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                if (parsed is < 1 or > PlayerbotChaosEngine.MaxBurstCount) return false;
                probes = parsed;
                if (parts.Length == 2) targetBot = parts[1];
            }
            else if (parts.Length == 1)
            {
                targetBot = parts[0];
            }
            else
            {
                return false;
            }
        }

        (int bots, int executed, long faultsBefore, long faultsAfter) = feature.RunChaosBurst(probes, targetBot);
        if (bots == 0)
        {
            context.Reply(targetBot is null
                ? "No running in-world bots available for chaos burst."
                : $"Bot '{targetBot}' is not running in the world.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Chaos burst complete: {executed} probes across {bots} bot(s), new faults={faultsAfter - faultsBefore} (total faults={faultsAfter}, malformedRejected={feature.Chaos.MalformedRejected}, invariantRepairs={feature.Chaos.InvariantRepairs})."));
        return true;
    }

    private static bool Assign(CommandContext context, string args)
    {
        string target = args.Trim();
        if (target.Length == 0 || target.Contains(' ', StringComparison.Ordinal)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        if (!feature.TryResolveBotId(target, out Guid botId, out string botName))
        {
            context.Reply($"Unknown bot '{target}'.");
            return true;
        }

        feature.Chaos.AssignBot(botId);
        context.Reply($"Bot {botName} ({botId}) assigned to chaos probes.");
        return true;
    }

    private static bool Unassign(CommandContext context, string args)
    {
        string target = args.Trim();
        if (target.Length == 0 || target.Contains(' ', StringComparison.Ordinal)) return false;
        if (FeatureOf(context) is not { } feature) return true;
        if (!feature.TryResolveBotId(target, out Guid botId, out string botName))
        {
            context.Reply($"Unknown bot '{target}'.");
            return true;
        }

        bool removed = feature.Chaos.UnassignBot(botId);
        context.Reply(removed
            ? $"Bot {botName} ({botId}) unassigned from chaos probes."
            : $"Bot {botName} ({botId}) was not explicitly assigned.");
        return true;
    }

    private static bool Faults(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (FeatureOf(context) is not { } feature) return true;
        ChaosEngineStatus status = feature.Chaos.Status();
        if (status.RecentFaults.Count == 0)
        {
            context.Reply("No chaos faults recorded.");
            return true;
        }

        CultureInfo c = CultureInfo.InvariantCulture;
        foreach (ChaosFaultRecord fault in status.RecentFaults)
        {
            context.Reply(string.Create(c,
                $"{fault.AtUtc:HH:mm:ss} {fault.BotName} ({fault.BotId}) {fault.Opcode} [{fault.Category}/{fault.ProbeName} len={fault.PayloadLength} hex={fault.PayloadHex}] -> {fault.ExceptionType}: {fault.Message}"));
        }
        return true;
    }

    private static bool Clear(CommandContext context, string args)
    {
        string word = args.Trim();
        bool clearAll = false;
        if (word.Length != 0)
        {
            if (!word.Equals("all", StringComparison.OrdinalIgnoreCase)) return false;
            clearAll = true;
        }

        if (FeatureOf(context) is not { } feature) return true;
        feature.Chaos.ClearStats(clearAssignments: clearAll);
        context.Reply(clearAll
            ? "Chaos stats, recent faults and explicit bot assignments cleared."
            : "Chaos stats and recent faults cleared.");
        return true;
    }

    internal static IEnumerable<string> FormatStatus(ChaosEngineStatus status, int runningBots)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        yield return string.Create(c,
            $"Playerbot chaos: {(status.Enabled ? "on" : "off")}, activeBots={status.ConfiguredActiveBots} (running={runningBots}, assigned={status.AssignedBots}), probesPerThink={status.ProbesPerThink}, seed={status.Seed}");
        yield return string.Create(c,
            $"Switches: malformedPayloads={(status.MalformedPayloads ? "on" : "off")}, stateRaces={(status.StateRaces ? "on" : "off")}, boundaryMovement={(status.BoundaryMovement ? "on" : "off")}");
        yield return string.Create(c,
            $"Totals: probes={status.ProbesExecuted}, malformedRejected={status.MalformedRejected}, faultsCaught={status.FaultsCaught}, invariantRepairs={status.InvariantRepairs}");

        string cats = string.Join(", ", status.ProbesByCategory.Select(kv => string.Create(c, $"{kv.Key}={kv.Value}")));
        yield return $"By category: {cats}";

        if (status.AssignedBotIds.Count > 0)
            yield return $"Assigned bots: {string.Join(", ", status.AssignedBotIds)}";

        if (status.RecentFaults.Count > 0)
        {
            ChaosFaultRecord last = status.RecentFaults[^1];
            yield return string.Create(c,
                $"Last fault: {last.AtUtc:HH:mm:ss} {last.BotName} {last.Opcode} [{last.Category}/{last.ProbeName}] -> {last.ExceptionType}: {last.Message}");
        }
    }
}

using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.ServerConfig;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// <c>.packetcheck status | on | off | bots on|off | staff on|off | reset</c> (Administrator; works from the console and remote administration):
/// the runtime switch of the report-only packet heuristics (docs/areas/packet-heuristics.md). A change lasts until the next restart, which
/// returns to <c>World:PacketHeuristics</c>. <c>bots on</c> includes the managed playerbot sessions in the checks, <c>bots off</c> exempts them;
/// <c>staff</c> does the same for staff accounts. <c>reset</c> zeroes the counters and lets every session report again at once.
/// </summary>
public sealed class PacketHeuristicsCommands : ICommandGroup
{
    /// <inheritdoc/>
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("packetcheck", AccountSecurity.Administrator, "Report-only packet heuristics (findings go to the anomaly ledger under packet/*).", Children:
        [
            new ChatCommand("status", AccountSecurity.Administrator, "Syntax: .packetcheck status\nShow whether the checks run, who is exempt and the per-check counts of player, bot and staff traffic.", Status),
            new ChatCommand("on", AccountSecurity.Administrator, "Syntax: .packetcheck on\nStart the checks until the next restart.", On),
            new ChatCommand("off", AccountSecurity.Administrator, "Syntax: .packetcheck off\nStop the checks until the next restart. Counters and ledger findings stay.", Off),
            new ChatCommand("bots", AccountSecurity.Administrator, "Syntax: .packetcheck bots on|off\nOn: check the managed playerbot sessions too. Off: exempt them (the default).", Bots),
            new ChatCommand("staff", AccountSecurity.Administrator, "Syntax: .packetcheck staff on|off\nOn: check staff accounts and GM-mode players too. Off: exempt them (the default).", Staff),
            new ChatCommand("reset", AccountSecurity.Administrator, "Syntax: .packetcheck reset\nZero the counters and let every session report again at once.", Reset),
        ]),
    ];

    private static PacketHeuristicsFeature? FeatureOf(CommandContext context)
    {
        PacketHeuristicsFeature? feature = context.Session.Services.GetService<PacketHeuristicsFeature>();
        if (feature is null)
        {
            context.Reply("Packet heuristics are not available on this server.");
        }

        return feature;
    }

    private static bool Status(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            return true;
        }

        foreach (string line in feature.StatusLines())
        {
            context.Reply(line);
        }

        return true;
    }

    private static bool On(CommandContext context, string text) => Switch(context, "on", "packetcheck.enabled", true, null);

    private static bool Off(CommandContext context, string text) => Switch(context, "off", "packetcheck.enabled", false, null);

    private static bool Reset(CommandContext context, string text)
    {
        if (FeatureOf(context) is not { } feature)
        {
            return true;
        }

        feature.Reset();
        context.Reply("Packet heuristics: counters zeroed; every session reports again at once.");
        context.Session.Logger.LogInformation("Packet heuristics reset by {Invoker}", context.InvokerName);
        return true;
    }

    private static bool Bots(CommandContext context, string text) => Toggle(context, text, "bots", "packetcheck.exemptbots");

    private static bool Staff(CommandContext context, string text) => Toggle(context, text, "staff", "packetcheck.exemptstaff");

    /// <summary>Sets a key through the same posted set as <c>.server config set</c>, then prints the status (or the refusal).</summary>
    private static bool Switch(CommandContext context, string state, string key, bool value, string? what)
    {
        if (FeatureOf(context) is null)
        {
            return true;
        }

        ServerConfigCommands.PostSet(context, key, value ? "true" : "false", null, outcome =>
        {
            if (outcome.Result != OptionSetResult.Ok)
            {
                context.Reply($"Packet heuristics: {outcome.Error}.");
                return;
            }

            if (what is null)
            {
                context.Session.Logger.LogInformation("Packet heuristics switched {State} by {Invoker} (runtime only)", state, context.InvokerName);
            }
            else
            {
                context.Session.Logger.LogInformation("Packet heuristics: {What} {State} by {Invoker} (runtime only)", what, state, context.InvokerName);
            }

            Status(context, string.Empty);
        });
        return true;
    }

    private static bool Toggle(CommandContext context, string text, string what, string exemptKey)
    {
        string? word = new CommandArgs(text).ExtractArg();
        bool include;
        if (string.Equals(word, "on", StringComparison.OrdinalIgnoreCase))
        {
            include = true;
        }
        else if (string.Equals(word, "off", StringComparison.OrdinalIgnoreCase))
        {
            include = false;
        }
        else
        {
            return false;
        }

        // "bots on" includes the sessions in the checks; the key says the opposite (exempt).
        return Switch(context, include ? "included" : "exempt", exemptKey, !include, what);
    }
}

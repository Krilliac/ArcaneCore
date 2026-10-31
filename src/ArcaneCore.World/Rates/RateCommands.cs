using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;

namespace ArcaneCore.World.Rates;

/// <summary>
/// <c>.rates</c>: view and change the server rates live (<see cref="RateCatalog"/>), the experience, drop, money, reputation, regeneration and
/// creature rates of the vmangos <c>Rate.*</c> options. The sibling of <c>.movement</c> (the player speed rates, which stay there): a GameMaster
/// may show, an Administrator may set and reset, because one set changes the game for every player at once. A set lasts until
/// <c>.rates reset</c>, <c>.reload config</c> or a restart; nothing is written to a configuration file (to keep a value, edit the file, see
/// docs/areas/rates.md). Every run is in the GM command audit (<c>CommandTable.Execute</c>) and every change is logged and published by
/// <see cref="ServerRates"/>. This is not <c>.modify xprate</c>, which sets one player's personal rate.
/// </summary>
public sealed class RateCommands : ICommandGroup
{
    public const string ShowSyntax = "Syntax: .rates [show] [$filter]\nShow the live-tunable server rates: the value in force, the configured value and whether it is overridden. "
        + "$filter: part of a name (xp, drop, creature...).";

    public const string SetSyntax = "Syntax: .rates set $name $value [$reason]\nChange a server rate for everyone until .rates reset, .reload config or restart (not saved to any file). "
        + "$name: a vmangos name such as Rate.XP.Kill or a short form (xp, xp.quest, drop.money, money, drop.epic...), any case. $value: 0 to 100 (1 is retail), "
        + "some rates have their own bounds; .rates show lists them. $reason is logged with the change.";

    public const string ResetSyntax = "Syntax: .rates reset [$name|all]\nPut one rate (or all of them, the default) back to its configured value.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("rates", AccountSecurity.GameMaster, "Live server rates. Syntax: .rates [show|set|reset]", Show, Children:
        [
            new ChatCommand("show", AccountSecurity.GameMaster, ShowSyntax, Show),
            new ChatCommand("set", AccountSecurity.Administrator, SetSyntax, Set),
            new ChatCommand("reset", AccountSecurity.Administrator, ResetSyntax, Reset),
        ]),
    ];

    private static RateScope ScopeOf(CommandContext context)
        => new(context.Session.Services, context.World, context.Session.Logger);

    private static string Describe(RateStatus status)
    {
        RateDefinition rate = status.Rate;
        if (!status.Available)
        {
            return $"{rate.Name}: not available (its feature is not running)";
        }

        string bounds = rate.Min > 0f || rate.Max != 100f ? $", {ServerRates.Show(rate.Min)}-{ServerRates.Show(rate.Max)}" : string.Empty;
        string state = status.Overridden ? $"OVERRIDDEN, configured {ServerRates.Show(status.Configured)}" : "configured";
        string applies = rate.AppliesTo == "live" ? string.Empty : $"; applies to {rate.AppliesTo}";
        return $"{rate.Name} = {ServerRates.Show(status.Current)} ({state}{bounds}): {rate.Description}{applies}";
    }

    private static bool Show(CommandContext context, string text)
    {
        string filter = text.Trim();
        if (filter.StartsWith("show", StringComparison.OrdinalIgnoreCase) && (filter.Length == 4 || char.IsWhiteSpace(filter[4])))
        {
            filter = filter[4..].Trim();
        }

        RateScope scope = ScopeOf(context);
        ServerRates rates = ServerRates.For(context.World);
        var lines = new List<string>();
        foreach (RateDefinition rate in ServerRates.All)
        {
            if (filter.Length == 0
                || rate.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || rate.Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(Describe(rates.Describe(scope, rate)));
            }
        }

        if (lines.Count == 0)
        {
            context.Reply($".rates: no rate matches '{filter}'.");
            return true;
        }

        context.Reply($"Server rates ({lines.Count}, {rates.OverrideCount} overridden; 1 is retail; .rates set changes one live):\n" + string.Join('\n', lines));
        return true;
    }

    private static bool Set(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractLiteral();
        if (name is null || !args.ExtractFloat(out float value))
        {
            return false;
        }

        string? reason = args.IsEmpty ? null : args.Rest.Trim();
        if (ServerRates.Find(name) is not { } rate)
        {
            context.Reply($".rates set: unknown rate '{name}'. .rates show lists the names.");
            return true;
        }

        RateScope scope = ScopeOf(context);
        ServerRates rates = ServerRates.For(context.World);
        RateStatus before = rates.Describe(scope, rate);
        if (rates.TrySet(scope, rate, value, context.InvokerName, reason, out string? error) != RateSetResult.Ok)
        {
            context.Reply($".rates set: {error}.");
            return true;
        }

        context.Reply($"Rate {rate.Name}: {ServerRates.Show(before.Current)} -> {ServerRates.Show(value)} "
            + $"(configured {ServerRates.Show(before.Configured)}; until .rates reset, .reload config or restart; not saved to any file)."
            + (rate.AppliesTo == "live" ? string.Empty : $" Applies to {rate.AppliesTo}."));
        return true;
    }

    private static bool Reset(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractLiteral();
        if (!args.IsEmpty)
        {
            return false;
        }

        RateScope scope = ScopeOf(context);
        ServerRates rates = ServerRates.For(context.World);
        if (name is null || name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            int count = rates.ResetAll(scope, context.InvokerName);
            context.Reply(count == 0 ? "No rate is overridden." : $"{count} rate(s) back to their configured values.");
            return true;
        }

        if (ServerRates.Find(name) is not { } rate)
        {
            context.Reply($".rates reset: unknown rate '{name}'. .rates show lists the names.");
            return true;
        }

        if (!rates.TryReset(scope, rate, context.InvokerName, RateChangeKind.Reset, out string? refused))
        {
            context.Reply(refused ?? $"{rate.Name} is not overridden.");
            return true;
        }

        context.Reply($"{rate.Name} is back to its configured value {ServerRates.Show(rates.Describe(scope, rate).Current)}.");
        return true;
    }
}

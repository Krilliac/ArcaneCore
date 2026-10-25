using System.Globalization;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.ServerConfig;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Locomotion;

/// <summary>
/// <c>.movement rates</c> and <c>.movement set $field $value</c>: view and change the player speed rates at runtime (the MaNGOS Zero fork's
/// <c>.movement config</c> / <c>.movement set speedrate|run|swim|walk</c>, ChatCommands/MovementCommands.cpp, which take percents; here the
/// multiplier of the <c>Locomotion:Player*SpeedRate</c> options). No retail core has a <c>.movement</c> root. Both need Administrator: a set
/// changes every online player's speeds at once (the fork's DoForAllPlayers refresh, here <see cref="SpeedRates.ApplyToAll"/>). Every run is
/// written to the GM command audit (<c>CommandTable.Execute</c>) and a set is also logged with the old and new value. A set lasts until the
/// next <c>.reload config</c> or restart, which take the configuration file's value again.
/// </summary>
public sealed class MovementRateCommands : ICommandGroup
{
    /// <summary>
    /// The <c>.movement set</c> fields: the name typed, the reload key (<c>Locomotion:...</c>) it changes and how that key reads and writes the
    /// option. <c>.server config</c> answers to <c>movement.$field</c> for every row (<see cref="LegacyOptionSources.MovementAlias"/>), so a new row
    /// needs a matching <c>WorldConfigKeys</c> entry; a test fails when a row has none.
    /// </summary>
    internal static readonly (string Field, string Path, Func<LocomotionOptions, float> Get, Action<LocomotionOptions, float> Set)[] Fields =
    [
        ("speedrate", "Locomotion:PlayerSpeedRate", o => o.PlayerSpeedRate, (o, v) => o.PlayerSpeedRate = v),
        ("run", "Locomotion:PlayerRunSpeedRate", o => o.PlayerRunSpeedRate, (o, v) => o.PlayerRunSpeedRate = v),
        ("runback", "Locomotion:PlayerRunBackSpeedRate", o => o.PlayerRunBackSpeedRate, (o, v) => o.PlayerRunBackSpeedRate = v),
        ("swim", "Locomotion:PlayerSwimSpeedRate", o => o.PlayerSwimSpeedRate, (o, v) => o.PlayerSwimSpeedRate = v),
        ("swimback", "Locomotion:PlayerSwimBackSpeedRate", o => o.PlayerSwimBackSpeedRate, (o, v) => o.PlayerSwimBackSpeedRate = v),
        ("walk", "Locomotion:PlayerWalkSpeedRate", o => o.PlayerWalkSpeedRate, (o, v) => o.PlayerWalkSpeedRate = v),
        ("turn", "Locomotion:PlayerTurnRate", o => o.PlayerTurnRate, (o, v) => o.PlayerTurnRate = v),
    ];

    public const string SetSyntax = "Syntax: .movement set $field $value\nChange a player speed rate for every online player until the next .reload config or restart. "
        + "$field: speedrate (all speeds), run, runback, swim, swimback, walk or turn. $value: a multiplier from 0.1 to 10 (1 is retail).";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("movement", AccountSecurity.Administrator, "Player movement rates. Syntax: .movement $subcommand", Children:
        [
            new ChatCommand("rates", AccountSecurity.Administrator, "Syntax: .movement rates\nShow the player speed rates in force (1 is retail).", Rates),
            new ChatCommand("set", AccountSecurity.Administrator, SetSyntax, Set),
        ]),
    ];

    /// <summary>The line <c>.movement rates</c> prints.</summary>
    public static string Describe(LocomotionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlayerSpeedRates effective = options.GetSpeedRates();
        return string.Create(CultureInfo.InvariantCulture,
            $"Movement rates: speedrate {options.PlayerSpeedRate:0.###} x (run {options.PlayerRunSpeedRate:0.###}, runback {options.PlayerRunBackSpeedRate:0.###}, "
            + $"swim {options.PlayerSwimSpeedRate:0.###}, swimback {options.PlayerSwimBackSpeedRate:0.###}, walk {options.PlayerWalkSpeedRate:0.###}), turn {options.PlayerTurnRate:0.###}.\n"
            + $"Effective: run {effective.Run:0.###}, runback {effective.RunBack:0.###}, swim {effective.Swim:0.###}, swimback {effective.SwimBack:0.###}, "
            + $"walk {effective.Walk:0.###}, turn {effective.Turn:0.###}.");
    }

    private static bool Rates(CommandContext context, string text)
    {
        if (LocomotionEnvironment.RegisteredOptions(context.World) is not { } options)
        {
            context.Reply("Movement rates are not available: the locomotion feature is not running.");
            return true;
        }

        context.Reply(Describe(options));
        return true;
    }

    private static bool Set(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? field = args.ExtractLiteral();
        string? value = args.ExtractLiteral();
        if (field is null || value is null || !args.IsEmpty)
        {
            return false;
        }

        int index = Array.FindIndex(Fields, f => f.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            context.Reply($".movement set: unknown field '{field}'.");
            return false;
        }

        if (LocomotionEnvironment.RegisteredOptions(context.World) is null)
        {
            context.Reply("Movement rates are not available: the locomotion feature is not running.");
            return true;
        }

        // The same set as .server config set (the keys answer to movement.$field), run in World.Post. It checks the rate against the key's
        // bounds and re-sends every online player's speeds (SpeedRates.ApplyToAll) when it changes.
        string name = Fields[index].Field;
        ServerConfigCommands.PostSet(context, "movement." + name, value, null, outcome =>
        {
            if (outcome.Result != OptionSetResult.Ok)
            {
                bool wellFormed = outcome.Key is { } key && outcome.Result == OptionSetResult.Invalid && key.TryParse(value, out _, out _);
                context.Reply(wellFormed ? $".movement set: {value} is out of range: {outcome.Error}." : $".movement set: {outcome.Error}.");
                return;
            }

            int refreshed = context.World.OnlinePlayers.Count();
            context.Session.Logger.LogWarning(
                "{Player} (account {Account}) set movement rate {Field} from {Old} to {New}; speeds of {Count} online player(s) re-sent",
                context.InvokerName, context.Session.AccountId, name, outcome.Before, outcome.After, refreshed);
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Movement: {name} {outcome.Before} -> {outcome.After} (until .reload config or restart); speeds of {refreshed} online player(s) re-sent."));
        });
        return true;
    }
}

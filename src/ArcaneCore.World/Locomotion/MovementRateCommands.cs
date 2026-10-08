using System.Globalization;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
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
    private static readonly (string Field, Func<LocomotionOptions, float> Get, Action<LocomotionOptions, float> Set)[] Fields =
    [
        ("speedrate", o => o.PlayerSpeedRate, (o, v) => o.PlayerSpeedRate = v),
        ("run", o => o.PlayerRunSpeedRate, (o, v) => o.PlayerRunSpeedRate = v),
        ("runback", o => o.PlayerRunBackSpeedRate, (o, v) => o.PlayerRunBackSpeedRate = v),
        ("swim", o => o.PlayerSwimSpeedRate, (o, v) => o.PlayerSwimSpeedRate = v),
        ("swimback", o => o.PlayerSwimBackSpeedRate, (o, v) => o.PlayerSwimBackSpeedRate = v),
        ("walk", o => o.PlayerWalkSpeedRate, (o, v) => o.PlayerWalkSpeedRate = v),
        ("turn", o => o.PlayerTurnRate, (o, v) => o.PlayerTurnRate = v),
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
        if (field is null || !args.ExtractFloat(out float value) || !args.IsEmpty)
        {
            return false;
        }

        int index = Array.FindIndex(Fields, f => f.Field.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            context.Reply($".movement set: unknown field '{field}'.");
            return false;
        }

        if (!(value >= LocomotionOptions.MinSpeedRate && value <= LocomotionOptions.MaxSpeedRate))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $".movement set: {value} is out of range; the rate must be between {LocomotionOptions.MinSpeedRate} and {LocomotionOptions.MaxSpeedRate}."));
            return true;
        }

        if (LocomotionEnvironment.RegisteredOptions(context.World) is not { } options)
        {
            context.Reply("Movement rates are not available: the locomotion feature is not running.");
            return true;
        }

        (string name, Func<LocomotionOptions, float> get, Action<LocomotionOptions, float> set) = Fields[index];
        float old = get(options);
        set(options, value);
        int refreshed = SpeedRates.ApplyToAll(context.World, options);
        context.Session.Logger.LogWarning(
            "{Player} (account {Account}) set movement rate {Field} from {Old} to {New}; speeds of {Count} online player(s) re-sent",
            context.Player.Name, context.Session.AccountId, name, old, value, refreshed);
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Movement: {name} {old:0.###} -> {value:0.###} (until .reload config or restart); speeds of {refreshed} online player(s) re-sent."));
        return true;
    }
}

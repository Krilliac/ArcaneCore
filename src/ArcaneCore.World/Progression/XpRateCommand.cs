using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Progression;

/// <summary>
/// <c>.modify xprate #rate</c> (vmangos ChatHandler::HandleModifyXpRateCommand, CharacterCommands.cpp:62-92, SEC_PLAYER, Chat.cpp:630): a player sets
/// its own personal XP rate (<see cref="Player.PersonalXpRate"/>, multiplied into every XP gain by Player::GiveXP); a GameMaster or higher sets the
/// selected player's (or its own without a selection), subject to the security hierarchy. The rate may not be below
/// <c>Progression:RateXpPersonalMin</c> and, below GameMaster, not above <c>Progression:RateXpPersonalMax</c> (Rate.XP.Personal.Min/Max, both 1 by
/// default, so a stock realm only lets players set 1). The rate lasts until logout, as vmangos never saves it.
/// </summary>
public sealed class XpRateCommand : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("xprate", AccountSecurity.Player,
            "Syntax: .modify xprate #rate\nSet your experience rate (a game master sets the selected player's) to #rate times normal experience gain.",
            Handle, RetailLevel: 0),
    ];

    /// <summary>vmangos mangos_string 177 LANG_XP_RATE_MIN.</summary>
    public static string BelowMin(float min) => $"You can't set XP rate below {Show(min)}!";

    /// <summary>vmangos mangos_string 178 LANG_XP_RATE_MAX.</summary>
    public static string AboveMax(float max) => $"You can't set XP rate above {Show(max)}!";

    /// <summary>vmangos mangos_string 179 LANG_XP_RATE_SET.</summary>
    public static string RateSet(string whose, float rate) => $"You have changed {whose} XP rate to {Show(rate)} times normal experience gain.";

    private static string Show(float value) => value.ToString(CultureInfo.InvariantCulture);

    private static bool Handle(CommandContext context, string text)
    {
        bool gameMaster = context.Security >= AccountSecurity.GameMaster;
        Player? target = gameMaster ? context.SelectedPlayerOrSelf() : context.Player;
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return true;
        }

        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float rate) || !float.IsFinite(rate))
        {
            return false;
        }

        if (!ReferenceEquals(target, context.Player) && !context.CanActOn(target))
        {
            return true;
        }

        ProgressionOptions options = context.Session.Services.GetService<ProgressionFeature>()?.Progression.Options ?? new ProgressionOptions();
        float min = options.RateXpPersonalMin >= 0.0f ? options.RateXpPersonalMin : 1.0f; // setConfigMin(..., 1.0f, 0.0f)
        float max = options.RateXpPersonalMax >= 0.0f ? options.RateXpPersonalMax : 1.0f;
        if (rate < min)
        {
            context.Reply(BelowMin(min));
            return false;
        }

        if (rate > max && !gameMaster)
        {
            context.Reply(AboveMax(max));
            return false;
        }

        target.PersonalXpRate = rate;
        context.Reply(RateSet(ReferenceEquals(target, context.Player) ? "your" : target.Name + "'s", rate));
        return true;
    }
}

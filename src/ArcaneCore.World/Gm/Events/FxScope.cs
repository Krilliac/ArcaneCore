using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Events;

/// <summary>Who receives an <c>.fx</c> effect (ArcaneCore GM tooling; not a vmangos concept).</summary>
public enum FxScope
{
    /// <summary>The invoker only (the default).</summary>
    Self,

    /// <summary>The selected online player.</summary>
    Target,

    /// <summary>Every player in the invoker's zone on the invoker's map instance.</summary>
    Zone,

    /// <summary>Every player on the invoker's map instance (Administrator).</summary>
    Map,

    /// <summary>Every online player (Administrator).</summary>
    Server,
}

/// <summary>
/// The shared recipient helper of the <c>.fx</c> commands: parses the scope word and resolves it to the online
/// players that receive the packet. <c>self</c>, <c>target</c> and <c>zone</c> need the command's own level
/// (GameMaster); <c>map</c> and <c>server</c> need the retail level of an Administrator account so cosmetic
/// spam cannot reach the whole realm from a GameMaster account. <c>target</c> applies the usual
/// <see cref="CommandContext.CanActOn"/> rule.
/// </summary>
public static class FxScopes
{
    /// <summary>The scope words in the order the help lists them.</summary>
    public static readonly IReadOnlyList<string> Words = ["self", "target", "zone", "map", "server"];

    /// <summary>The reply when <c>target</c> is used with nothing (or a non-player) selected.</summary>
    public const string NoPlayerSelected = GmStrings.NoCharSelected;

    /// <summary>Parse a scope word (case-insensitive); false for anything else.</summary>
    public static bool TryParse(string? word, out FxScope scope)
    {
        scope = FxScope.Self;
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        int index = Words.ToList().FindIndex(w => w.Equals(word, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        scope = (FxScope)index;
        return true;
    }

    /// <summary>The lower-case word of a scope.</summary>
    public static string Word(FxScope scope) => Words[(int)scope];

    /// <summary>Whether <paramref name="scope"/> needs an Administrator account.</summary>
    public static bool NeedsAdministrator(FxScope scope) => scope is FxScope.Map or FxScope.Server;

    /// <summary>
    /// The players <paramref name="scope"/> names, or null after replying why there are none (security too low,
    /// no player selected, the selected player outranks the invoker). World thread.
    /// </summary>
    public static IReadOnlyList<Player>? Resolve(CommandContext context, FxScope scope)
    {
        if (NeedsAdministrator(scope)
            && context.Commands.Gm.LevelOf(context.Security) < context.Commands.Gm.LevelOf(AccountSecurity.Administrator))
        {
            context.Reply(GmStrings.SecurityTooLow);
            return null;
        }

        Player self = context.Player;
        switch (scope)
        {
            case FxScope.Self:
                return [self];
            case FxScope.Target:
                if (self.Selection.IsEmpty || context.World.FindOnlinePlayer(self.Selection) is not { } target)
                {
                    context.Reply(NoPlayerSelected);
                    return null;
                }

                return context.CanActOn(target) ? [target] : null;
            case FxScope.Zone:
                return self.Map is { } zoneMap
                    ? [.. zoneMap.Players.Where(p => p.ZoneId == self.ZoneId)]
                    : [self];
            case FxScope.Map:
                return self.Map is { } map ? [.. map.Players] : [self];
            default:
                return [.. context.World.OnlinePlayers];
        }
    }
}

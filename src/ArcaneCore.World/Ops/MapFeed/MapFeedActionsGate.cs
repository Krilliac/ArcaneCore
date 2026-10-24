using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Whether the action channel is on, decided once when the feed starts (docs/areas/atlas-actions.md, "Switching it on"), and why not.
/// </summary>
/// <param name="On">Actions are accepted.</param>
/// <param name="OffCode">The code an <c>action</c> is refused with while off (<c>actions-off</c> or <c>live-server</c>), else null.</param>
/// <param name="WhyOff">A sentence for <c>caps</c>, else null.</param>
/// <param name="LiveServer">The process carries the live-server marker.</param>
/// <param name="Login">Web actors are accepted: on, <c>WebLogin</c> set, the true-console predicate present and a ticket service registered.</param>
internal sealed record ActionsGate(bool On, string? OffCode, string? WhyOff, bool LiveServer, bool Login)
{
    /// <summary>The type the true-console fix adds (design 1.7, <c>claude/fix-remote-console-bypass</c>); its presence in this assembly is the probe for web login.</summary>
    public const string TrueConsolePredicateType = "ArcaneCore.World.Commands.CommandContextConsole";

    /// <summary>Whether this build carries the true-console predicate.</summary>
    public static bool PredicatePresent => typeof(WorldMapFeed).Assembly.GetType(TrueConsolePredicateType) is not null;

    /// <summary>Not yet decided: the feed has not started.</summary>
    public static ActionsGate NotStarted { get; } = new(false, ActionCodes.ActionsOff, "the map feed has not started", false, false);

    /// <summary>
    /// Decide from the options, the live-server marker, whether a ticket service exists and whether the predicate is present; log the decision
    /// (Warning for everything an operator must see: refusal on the live server, the live-server opt-in, a web login that cannot be offered).
    /// </summary>
    public static ActionsGate Decide(MapFeedActionsOptions options, bool liveServer, bool hasTickets, bool predicatePresent, ILogger logger)
    {
        if (!options.Enabled)
        {
            return new ActionsGate(false, ActionCodes.ActionsOff, $"{MapFeedActionsOptions.SectionName}:Enabled is false", liveServer, false);
        }

        IReadOnlyList<string> problems = options.Problems();
        if (problems.Count > 0)
        {
            string why = "the configuration is invalid: " + string.Join("; ", problems);
            logger.LogWarning("map feed actions off: {Why}", why);
            return new ActionsGate(false, ActionCodes.ActionsOff, why, liveServer, false);
        }

        if (liveServer && !options.AllowOnLiveServer)
        {
            const string Why = "this is the live server (ARCANECORE_LIVE_SERVER is set); the action channel stays off here unless AllowOnLiveServer is set";
            logger.LogWarning("map feed actions refused: {Why}", Why);
            return new ActionsGate(false, ActionCodes.LiveServer, Why, true, false);
        }

        if (liveServer)
        {
            logger.LogWarning(
                "map feed actions ON on the live server (AllowOnLiveServer): the local actor runs commands as {Level}",
                options.LocalSecurity);
        }
        else
        {
            logger.LogInformation("map feed actions on: the local actor runs commands as {Level} (World:Control:MapFeed:Actions)", options.LocalSecurity);
        }

        bool login = false;
        if (options.WebLogin)
        {
            if (!predicatePresent)
            {
                logger.LogWarning(
                    "map feed actions: WebLogin is set but this build has no true-console predicate ({Type}, design 1.7); web actors are refused until the remote-console fix is merged",
                    TrueConsolePredicateType);
            }
            else if (!hasTickets)
            {
                logger.LogWarning("map feed actions: WebLogin is set but no web ticket service is registered (IFeedActorTickets); web actors are refused");
            }
            else
            {
                login = true;
                logger.LogInformation("map feed actions: web actors are accepted at up to {Level} (PublicMaxSecurity)", options.PublicMaxSecurity);
            }
        }

        return new ActionsGate(true, null, null, liveServer, login);
    }
}

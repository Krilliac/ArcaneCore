using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Battlegrounds.PlayerbotBattlegroundWorldTests;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Bots alone must be able to start Alterac Valley and fill it. The live row asks 20 players per team and takes 40 (battleground_template:
/// min 20, max 40) while <c>MaxBotsPerBattleground</c> defaults to 20, i.e. 10 per team, so the queue never reached its minimum.
/// <c>RaiseCapToFillTemplate</c> raises such a type's cap to 2 x the template maximum (80), and the server's queue invites queued players
/// into a preparing or running match with free slots (BattlegroundQueue.CheckFreeSlots, vmangos FillPlayersToBG), so bots keep queuing
/// until each team holds the maximum and backfill a leaver's slot. These are the same shapes at a small scale: a bot cap of 4 (2 per team)
/// against a template minimum of 3 per team. The fixture's other templates keep a minimum of 1, which hid it.
/// </summary>
public abstract class PlayerbotAlteracValleyMatchWorldTestBase
{
    private protected const uint MinPerTeam = 3;
    private protected const int BotsPerTeam = 3;

    private protected static async Task<ScenarioBot[]> LoginTeamsAsync(ScenarioContext context)
    {
        var bots = new List<ScenarioBot>();
        for (int i = 0; i < BotsPerTeam; i++) bots.Add(await AutonomousAsync(context, "Bgavmina" + (char)('a' + i), race: 1));
        for (int i = 0; i < BotsPerTeam; i++) bots.Add(await AutonomousAsync(context, "Bgavminh" + (char)('a' + i), race: 2));
        return [.. bots];
    }

    private protected static async Task<ScenarioBot[]> LoginAsync(ScenarioContext context, string prefix, int perTeam)
    {
        var bots = new List<ScenarioBot>();
        for (int i = 0; i < perTeam; i++) bots.Add(await AutonomousAsync(context, prefix + "a" + (char)('a' + i), race: 1));
        for (int i = 0; i < perTeam; i++) bots.Add(await AutonomousAsync(context, prefix + "h" + (char)('a' + i), race: 2));
        return [.. bots];
    }

    /// <summary>The match's participants per team (Alliance, Horde); world thread.</summary>
    private protected static (int Alliance, int Horde) TeamCounts(Battleground match)
    {
        int alliance = 0, horde = 0;
        foreach ((ObjectGuid _, Team team) in match.Participants())
        {
            if (team == Team.Alliance) alliance++;
            else horde++;
        }

        return (alliance, horde);
    }

    private protected static Task WaitInMatchAsync(ScenarioContext context, PlayerbotBattlegroundCoordinator coordinator, string what, ScenarioBot[] bots)
        => WaitExplainedAsync(context, what, () => bots.All(b => coordinator.Find(b.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch), TimeSpan.FromMinutes(5), bots);

    /// <summary>The bots of <paramref name="candidates"/> the coordinator follows now (any phase); world thread.</summary>
    private protected static IEnumerable<ScenarioBot> Followed(PlayerbotBattlegroundCoordinator coordinator, IEnumerable<ScenarioBot> candidates)
        => candidates.Where(b => coordinator.Find(b.BotId) is not null);
}

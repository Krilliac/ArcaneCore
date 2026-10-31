using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// One LFG line heard by many bots (built once, answered by at most the cap). Split from the former <c>PlayerbotRecruitWorldTests</c> so its tests run in parallel with the other parts; the world setup and helpers live in <see cref="PlayerbotRecruitWorldTestBase"/>.
/// </summary>
public sealed class PlayerbotRecruitOneLfgLineWorldTests : PlayerbotRecruitWorldTestBase
{
    /// <summary>One LFG line heard by many free bots: the line's data is built once, and no more than MaxLfgAnswersPerLine of the hearers get a place.</summary>
    [Fact]
    public async Task OneLfgLineHeardByManyBots_IsBuiltOnce_AndAnsweredByAtMostTheCap()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([EliteQuest], options =>
        {
            Still(options);
            options.Groups.Cooperation.RecruitRadiusYards = 1500;
            options.Groups.Cooperation.RecruitBaseChance = 1f;
            options.MaxBots = 40;
            options.MaxRegisteredBots = 60;
            options.Groups.Cooperation.MaxLfgAnswersPerLine = 4;
        });
        var (tank, _) = await WaitingPairAsync(world);
        var hearers = new List<(Guid Id, string Name)>();
        for (int i = 0; i < 24; i++)
            hearers.Add(await world.AddBotAsync("Hearer" + (char)('a' + (i / 5)) + (char)('a' + (i % 5)), Human, Priest, 10, [LesserHeal, Smite], South + new Vector3(i % 5, i / 5, 0)));

        (int builds, int pledged) = await world.OnWorldAsync(() =>
        {
            int before = world.Coordinator.LfgLineBuilds, answers = 0;
            foreach ((Guid id, string _) in hearers) // one line, one tick
                if (world.Coordinator.OfferLfg(id, tank.Id).Outcome == PlayerbotLfgOfferOutcome.Pledged) answers++;
            return (world.Coordinator.LfgLineBuilds - before, answers);
        });
        Assert.Equal(1, builds);
        Assert.Equal(4, pledged);
    }
}

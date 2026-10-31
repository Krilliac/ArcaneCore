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
/// Recruiting, bots answering bots' calls and guild calls (docs/areas/playbots-groups.md, Cooperation), end to end on the real world
/// handlers and the manual clock: a waiting group short of a healer asks a free bot nearby that lacks the quest, and the group's own
/// invitations, acceptances and quest share do the rest.
/// </summary>
public abstract class PlayerbotRecruitWorldTestBase
{
    private protected const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private protected const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private protected static readonly Vector3 South = new(StartX, StartY - 150f, StartZ);

    /// <summary>Too far from the quest giver for a bot to walk there and take the quest by itself within the test (so only a recruit can bring it into the group).</summary>
    private protected static readonly Vector3 FarSouth = new(StartX, StartY - 900f, StartZ);

    /// <summary>Bots crawl (they stay where the test puts them, and a bot far from the giver never takes the quest) and found no guilds of their own.</summary>
    private protected static void Still(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
    }

    private protected static void Eager(PlayerbotOptions options)
    {
        Still(options);
        options.Groups.Cooperation.RecruitWaitSeconds = 5;
        options.Groups.Cooperation.RecruitRadiusYards = 1500; // a waiting bot wanders off quickly; the radius has its own tests
        options.Groups.Cooperation.RecruitBaseChance = 1f;
    }

    /// <summary>A warrior and a mage that took the elite quest and wait for a third (and a healer).</summary>
    private protected static async Task<((Guid Id, string Name) Tank, (Guid Id, string Name) Mage)> WaitingPairAsync(GroupTestWorld world)
    {
        var tank = await world.AddBotAsync("Rectank", Human, Warrior, 10, [Taunt]);
        var mage = await world.AddBotAsync("Recmage", Human, Mage, 10, [Fireball]);
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.WaitingBots.Count() == 2), await world.TraceAsync());
        return (tank, mage);
    }

    private protected static PlayerbotRelation Relation(GroupTestWorld world, (Guid Id, string Name) bot, (Guid Id, string Name) other, short affinity, PlayerbotRelationFlags flags)
    {
        Player self = world.Player(bot.Id), them = world.Player(other.Id);
        return new PlayerbotRelation(bot.Id, (int)self.Guid.Low, them.Guid.Value, true, affinity, 6, 500, flags, world.Bots.Social.Memory.NowMs, "", "");
    }
}

using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Raid escalation (docs/areas/playbots-groups.md, Cooperation): a risk signal for a group beyond a party becomes a raid goal, the group
/// converts to a raid and recruits nearby bots up to <c>MaxRaidSize</c>; with too few bots around it gives up as a party goal does.
/// </summary>
public sealed class PlayerbotRaidEscalationWorldTests
{
    private const byte Human = 1, Warrior = 1, Priest = 5, Mage = 8;
    private const uint LesserHeal = 2050, Fireball = 133, Smite = 585;

    private static void Options(PlayerbotOptions options)
    {
        options.MoveSpeed = 0.1f;
        options.Social.Guilds.Enabled = false;
        options.Groups.Cooperation.RecruitWaitSeconds = 5;
        options.Groups.Cooperation.RecruitRadiusYards = 1500;
        options.Groups.Cooperation.RecruitBaseChance = 1f;
        options.Groups.FormationTimeoutSeconds = 60;
    }

    /// <summary>The world elite's bot asks for a group of eight (the risk estimate's signal, raised as the pull verdict would).</summary>
    private static async Task<(Guid Id, string Name)> TankSignalsAsync(GroupTestWorld world, int size)
    {
        var tank = await world.AddBotAsync("Raidtank", Human, Warrior, 10, [Taunt]);
        await world.OnWorldAsync(() =>
        {
            ArcaneCore.Game.Entities.Player player = world.Player(tank.Id);
            world.Bots.FindBrain(tank.Id)!.Risk.RaiseGroupSignal(new PlayerbotGroupSignal(OgreEntry, size, player.MapId,
                new Vector3(player.X, player.Y, player.Z), (uint)world.World.Host.World.NowMs));
            return true;
        });
        return tank;
    }

    private static async Task AddRaidersAsync(GroupTestWorld world, int mages)
    {
        await world.AddBotAsync("Raidpriest", Human, Priest, 10, [LesserHeal, Smite]);
        for (int i = 0; i < mages; i++) await world.AddBotAsync("Raidmage" + (char)('a' + i), Human, Mage, 10, [Fireball]);
    }

    [Fact]
    public async Task ASignalForEight_BecomesARaidGoal_ConvertsAndRecruitsEnoughBots()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], Options);
        await TankSignalsAsync(world, 8);
        await AddRaidersAsync(world, 6);

        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.Groups.Any(g => g.Goal.Size == 8 && g.Members.Count == 8)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Goal.Size == 8);
        Assert.True(await world.RunUntilAsync(60_000, () => group.Raid), await world.TraceAsync());
        Assert.All(group.Members, member => Assert.False(member.Real));
    }

    [Fact]
    public async Task ASignalForEight_WithTooFewBotsNearby_GivesUpAsAPartyGoalDoes()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], Options);
        await TankSignalsAsync(world, 8);
        await AddRaidersAsync(world, 1);

        Assert.True(await world.RunUntilAsync(120_000, () => world.Coordinator.Events.Any(e => e.Contains("found no partners", StringComparison.Ordinal))), await world.TraceAsync());
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Goal.Size == 8);
    }

    [Fact]
    public async Task WithRaidEscalationOff_ASignalForEight_StaysAPartyGoal()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], options =>
        {
            Options(options);
            options.Groups.Cooperation.RaidEscalation = false;
        });
        await TankSignalsAsync(world, 8);
        await AddRaidersAsync(world, 6);

        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.Groups.Any(g => g.Goal.Size == 5)), await world.TraceAsync());
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Goal.Size > 5);
    }

    [Theory]
    [InlineData(true, true, 40)]
    [InlineData(true, true, 20)]
    [InlineData(true, false, 5)]
    [InlineData(false, true, 5)]
    public void TheSignalLimit_IsAPartyUnlessARaidMayEscalate(bool escalation, bool raids, int expectedFor40)
    {
        var options = new PlayerbotGroupOptions { RaidsEnabled = raids };
        options.Cooperation.RaidEscalation = escalation;
        options.Cooperation.MaxRaidSize = expectedFor40 == 20 ? 20 : 40;
        Assert.Equal(expectedFor40, PlayerbotRecruitment.SignalLimit(options));
    }
}

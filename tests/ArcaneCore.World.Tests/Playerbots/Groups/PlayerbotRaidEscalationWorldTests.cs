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

    /// <summary>
    /// A rare elite (rank 2) beside the tank, too strong for a party: the tank's real risk estimate (PlayerbotRisk.Assess for a quest
    /// objective) raises the group signal, as it does when the brain pulls. Returns the signal's size.
    /// </summary>
    private static async Task<(Guid Id, string Name)> TankAssessesAsync(GroupTestWorld world, uint health, Action<int>? size = null)
    {
        var tank = await world.AddBotAsync("Raidtank", Human, Warrior, 10, [Taunt]);
        int needed = await world.OnWorldAsync(() =>
        {
            ArcaneCore.Game.Entities.Player player = world.Player(tank.Id);
            var system = player.Map!.FindUpdater<ArcaneCore.Game.Creatures.CreatureMapSystem>()!;
            var template = new ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate
            {
                Entry = 992100, Name = "Group Warlord", Faction = 14, CreatureType = 7, MinLevel = 10, MaxLevel = 10, DisplayIds = [903],
                MinLevelHealth = health, MaxLevelHealth = health, MinMeleeDamage = 10, MaxMeleeDamage = 14, MeleeBaseAttackTime = 2000, Rank = 2,
            };
            ArcaneCore.Game.Creatures.Creature mob = system.SpawnTemporary(template, player.X + 25f, player.Y, player.Z, 0);
            PlayerbotRisk risk = world.Bots.FindBrain(tank.Id)!.Risk;
            risk.Assess(player, mob, true, out _);
            return risk.GroupSignal?.Size ?? 0;
        });
        size?.Invoke(needed);
        return tank;
    }

    /// <summary>A rare elite that needs eight (found through the tank's own risk estimate) becomes a raid goal, converts, and recruits enough bots.</summary>
    [Fact]
    public async Task ARareEliteNeedingEight_ThroughTheRealRiskEstimate_BecomesARaid()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], Options);
        int needed = 0;
        await TankAssessesAsync(world, 10_000, n => needed = n);
        Assert.Equal(8, needed);
        await AddRaidersAsync(world, 6);

        Assert.True(await world.RunUntilAsync(90_000, () => world.Coordinator.Groups.Any(g => g.Goal.Size == 8 && g.Members.Count == 8)), await world.TraceAsync());
        PlayerbotGroupCoordinator.BotGroup group = world.Coordinator.Groups.Single(g => g.Goal.Size == 8);
        Assert.True(await world.RunUntilAsync(60_000, () => group.Raid), await world.TraceAsync());
    }

    /// <summary>The same raid goal with too few bots nearby gives up after FormationTimeoutSeconds, as a party goal does.</summary>
    [Fact]
    public async Task ARaidGoalFromTheRealRiskEstimate_WithTooFewBots_GivesUpAfterTheFormationTimeout()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], Options);
        await TankAssessesAsync(world, 10_000);
        long assessedAt = world.World.Host.World.NowMs;
        await AddRaidersAsync(world, 1);

        // Not before the formation timeout (the goal exists from the assessment on, so counting from it is the earliest it can run out), then soon after it.
        long timeoutMs = world.Options.Groups.FormationTimeoutSeconds * 1000L;
        await world.RunAsync((uint)Math.Max(0, assessedAt + timeoutMs - 5_000 - world.World.Host.World.NowMs));
        Assert.DoesNotContain(world.Coordinator.Events, e => e.Contains("found no partners", StringComparison.Ordinal));
        Assert.True(await world.RunUntilAsync(60_000, () => world.Coordinator.Events.Any(e => e.Contains("found no partners", StringComparison.Ordinal))), await world.TraceAsync());
        Assert.DoesNotContain(world.Coordinator.Groups, g => g.Goal.Size == 8);
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

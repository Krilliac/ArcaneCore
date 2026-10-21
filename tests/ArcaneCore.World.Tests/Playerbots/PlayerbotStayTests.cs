using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Combat;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>The scenario stay (<see cref="PlayerbotStay"/>): a bot waiting for its event scenario walks back to the stay and stands, defends itself, raises no PvP flag, and goes back to its own play when the lease runs out.</summary>
public sealed class PlayerbotStayTests
{
    private sealed record Rig(WorldTestHost Host, WorldSession Bot, WorldSession Enemy, PlayerbotBrain Brain)
    {
        public Player BotPlayer => Bot.Player!;

        public Player EnemyPlayer => Enemy.Player!;

        /// <summary>A stay centred where the bot stands now, live for 30 seconds of world time.</summary>
        public PlayerbotStay Stay(float radius = 30f, int id = 1)
            => new(BotPlayer.MapId, new Vector3(BotPlayer.X, BotPlayer.Y, BotPlayer.Z), radius, id, unchecked(Host.World.NowMs + 30_000));

        public void Think()
        {
            Bot.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);
            Bot.ManagedBudget = new ManagedActionBudget(4);
            Brain.Update(500);
        }
    }

    private static async Task Run(float gap, Func<Rig, Task> body)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldSession bot, WorldSession enemy) = await PlayerbotPvpTestKit.EnterPairAsync(host, gap);
        var brain = new PlayerbotBrain(bot, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } });
        try
        {
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(enemy.Player!.Guid), "the enemy is in sight");
            await body(new Rig(host, bot, enemy, brain));
        }
        finally
        {
            brain.Stop();
            bot.Kick();
            enemy.Kick();
            await bot.ManagedClosed;
            await enemy.ManagedClosed;
        }
    }

    private static bool Moving(Player player) => (player.Movement.Flags & MovementFlags.MaskMoving) != 0;

    [Fact]
    public Task AStayer_RaisesNoPvpFlag_AndScansNoPost_ButOwnsTheThink() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        var sent = new List<WorldOpcode>();
        rig.Bot.ManagedDispatchObserver = sent.Add;
        rig.Brain.Stay = rig.Stay();
        rig.Think();

        Assert.DoesNotContain(WorldOpcode.CmsgTogglePvp, sent);
        Assert.Equal(0u, (uint)(rig.BotPlayer.Flags & PlayerFlags.PvpDesired));
        Assert.Null(rig.Brain.InspectionPvpTarget); // a flagged enemy inside the radius is not engaged unprovoked
        Assert.Null(rig.BotPlayer.Combat.Victim);
        Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
        Assert.NotNull(rig.Brain.StayReport);
    }));

    [Fact]
    public Task AStayer_FightsBackAPlayerWhoAttacksIt() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        rig.Brain.Stay = rig.Stay();
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);
    }));

    [Fact]
    public Task AStayerAwayFromTheStay_WalksBack_ThenStandsStill() => Run(4f, async rig =>
    {
        await PartyTestHost.InstallFlatGroundAsync(rig.Host, await rig.Host.OnWorldAsync(() => rig.BotPlayer.Z));
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            rig.Brain.Stay = rig.Stay();
            bot.Relocate(bot.X - 100f, bot.Y, bot.Z, 0, rig.Host.World.NowMs); // beyond the radius, within the walking range
            rig.Think();
            Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
            Assert.True(Moving(bot), "the stayer walks back to its stay");
        });

        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            PlayerbotStay stay = rig.Brain.Stay!;
            bot.Relocate(stay.Centre.X, stay.Centre.Y, stay.Centre.Z, 0, rig.Host.World.NowMs);
            rig.Think();
            Assert.False(Moving(bot), "at the stay it stands");
            Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
        });
    });

    [Fact]
    public Task AStayerFarBeyondTheWalkingRange_StandsWhereItIs_ForTheOwnerToTeleport() => Run(4f, async rig =>
    {
        await PartyTestHost.InstallFlatGroundAsync(rig.Host, await rig.Host.OnWorldAsync(() => rig.BotPlayer.Z));
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            rig.Brain.Stay = rig.Stay();
            bot.Relocate(bot.X - (PlayerbotBrain.StayWalkYards + 300f), bot.Y, bot.Z, 0, rig.Host.World.NowMs);
            rig.Think();
            Assert.False(Moving(bot));
            Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
        });
    });

    [Fact]
    public Task AnExpiredLease_EndsTheStay_AndTheBotGoesBackToItsOwnPlay() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotStay lapsed = rig.Stay() with { UntilMs = unchecked(rig.Host.World.NowMs - 1) };
        rig.Brain.Stay = lapsed;
        rig.Think();
        Assert.Null(rig.Brain.Stay);
        Assert.Null(rig.Brain.StayReport);
    }));

    [Fact]
    public Task RenewingTheSameStay_KeepsTheIntent_ANewOneDropsIt() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        // The stayer swings at a player who attacked it: that fight is the intent a drop stops on the wire (CMSG_ATTACKSTOP).
        Player bot = rig.BotPlayer;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        PlayerbotStay stay = rig.Stay();
        rig.Brain.Stay = stay;
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);

        var sent = new List<WorldOpcode>();
        rig.Bot.ManagedDispatchObserver = sent.Add;
        rig.Bot.ManagedBudget = new ManagedActionBudget(4);
        rig.Brain.Stay = stay with { UntilMs = stay.UntilMs + 2_000 }; // the owner renews the lease
        Assert.Equal(stay.UntilMs + 2_000, rig.Brain.Stay!.UntilMs);
        Assert.DoesNotContain(WorldOpcode.CmsgAttackstop, sent);
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);

        rig.Brain.Stay = stay with { StayId = stay.StayId + 1 }; // another stay is a new errand: the old intent is dropped
        Assert.Contains(WorldOpcode.CmsgAttackstop, sent);
        Assert.Null(rig.Brain.InspectionPvpTarget);
        rig.Brain.Stay = null;
        Assert.Null(rig.Brain.Stay);
    }));
}

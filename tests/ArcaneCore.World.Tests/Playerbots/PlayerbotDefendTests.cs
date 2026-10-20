using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
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

/// <summary>Defend mode (<c>.playerbot defend</c>): a bot holds a post, fights the hostile players inside it, helps friendly creatures, heals its post and walks back.</summary>
public sealed class PlayerbotDefendTests
{
    private const uint GuardEntry = 993911;
    private const uint RaiderEntry = 993912;

    private sealed record Rig(WorldTestHost Host, WorldSession Bot, WorldSession Enemy, PlayerbotBrain Brain)
    {
        public Player BotPlayer => Bot.Player!;

        public Player EnemyPlayer => Enemy.Player!;

        public PlayerbotDefendPost Post(float radius, int id = 1)
            => new(BotPlayer.MapId, new Vector3(BotPlayer.X, BotPlayer.Y, BotPlayer.Z), radius, id);

        /// <summary>One think of the defender: no spells in the store (a plain swing), a fresh budget.</summary>
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

    [Fact]
    public Task DefenderTurnsOnPvpFlag() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        Assert.Equal(0u, (uint)(bot.Flags & PlayerFlags.PvpDesired));
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.NotEqual(0u, (uint)(bot.Flags & PlayerFlags.PvpDesired));
        Assert.NotEqual(0u, (uint)(bot.UnitFlags & UnitFlags.Pvp));
    }));

    [Fact]
    public Task DefenderEngagesFlaggedEnemyInsideRadius() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);
        Assert.Same(rig.EnemyPlayer, rig.BotPlayer.Combat.Victim);
    }));

    [Fact]
    public Task DroppingTheIntent_WhileSwingingAtAPlayer_SendsAttackStop() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.BotPlayer.Combat.Victim);

        var sent = new List<WorldOpcode>();
        rig.Bot.ManagedDispatchObserver = sent.Add;
        rig.Bot.ManagedBudget = new ManagedActionBudget(4);
        rig.Brain.DropIntent();

        Assert.Contains(WorldOpcode.CmsgAttackstop, sent);
        Assert.Null(rig.Brain.InspectionPvpTarget);
    }));

    [Fact]
    public Task DefenderIgnoresFlaggedEnemyOutsideRadius() => Run(25f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.Brain.Defend = rig.Post(10f);
        rig.Think();
        Assert.Null(rig.Brain.InspectionPvpTarget);
        Assert.Null(rig.BotPlayer.Combat.Victim);
    }));

    [Fact]
    public Task DefenderIgnoresUnflaggedEnemyInsideRadius() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Null(rig.Brain.InspectionPvpTarget);
        Assert.Null(rig.BotPlayer.Combat.Victim);
    }));

    [Fact]
    public Task DefenderAssistsFriendlyGuardInCombat() => Run(60f, async rig =>
    {
        Creature guard = null!, raider = null!;
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            bot.Map!.Combat.Hooks = new GuardHooks();
            guard = Spawn(rig.Host, bot, GuardEntry, 3f, 2f, "guard");
            raider = Spawn(rig.Host, bot, RaiderEntry, 2f, 0f, "raider");
            return true;
        });
        await rig.Host.WaitForWorldAsync(() => rig.BotPlayer.VisibleObjects.Contains(guard.Guid) && rig.BotPlayer.VisibleObjects.Contains(raider.Guid),
            "the guard and the raider are in sight");
        await rig.Host.OnWorldAsync(() =>
        {
            Map map = rig.BotPlayer.Map!;
            Assert.True(map.Combat.Attack(raider, guard));
            map.Combat.DealDamage(raider, guard, 1);
            Assert.Contains(raider, guard.Combat.Attackers);
            Assert.True(guard.Combat.IsInCombat);
            Assert.True(map.Combat.Hooks.IsFriendly(rig.BotPlayer, guard));
            Assert.False(rig.BotPlayer.Combat.IsInCombat);

            rig.Brain.Defend = rig.Post(40f);
            rig.Think();

            Assert.Same(raider, rig.Brain.InspectionPvpTarget);
            Assert.Same(raider, rig.BotPlayer.Combat.Victim);
        });
    });

    [Fact]
    public Task DefenderReturnsToPostAfterFight() => Run(4f, async rig =>
    {
        await PartyTestHost.InstallFlatGroundAsync(rig.Host, await rig.Host.OnWorldAsync(() => rig.BotPlayer.Z));
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            rig.Brain.Defend = rig.Post(40f);
            // The fight carried the bot 60 yards from its post (beyond 0.75 x the radius); nothing hostile is left in sight.
            bot.Relocate(bot.X - 60f, bot.Y, bot.Z, 0, rig.Host.World.NowMs);
            rig.Think();
            Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
            Assert.Null(rig.Brain.InspectionPvpTarget);
        });
    });

    [Fact]
    public async Task DefenderHealsWoundedDefenderOfSamePost()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession priest = await CombatTestSessions.EnterAsync(host, "DEFPRIEST", "Defpriest");
        WorldSession mate = await CombatTestSessions.EnterAsync(host, "DEFMATE", "Defmate");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "DEFSTRANG", "Defstrang");
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = false } };
        var healer = new PlayerbotBrain(priest, options);
        var mateBrain = new PlayerbotBrain(mate, options);
        var strangerBrain = new PlayerbotBrain(stranger, options);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player me = priest.Player!;
                me.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
                SpellFeature feature = priest.Services.GetRequiredService<SpellFeature>();
                feature.Spellbook.LearnSpell(me, SpellTestServices.Heal);
                SpellInfo selfOnly = feature.System.Store.Get(SpellTestServices.Heal)!;
                SpellInfo friendly = selfOnly with
                {
                    Id = 9201, Name = "Test Defend Heal",
                    Effects = [.. selfOnly.Effects.Select(e => e with { TargetA = SpellImplicitTarget.UnitFriend, BasePoints = 100_000 })],
                };
                feature.System.Store = new SpellStore(feature.System.Store.All.Append(friendly), [], []);
                Assert.True(feature.Spellbook.LearnSpell(me, friendly.Id));
                healer.CombatSpells.Reset();

                var centre = new Vector3(me.X, me.Y, me.Z);
                healer.Defend = new PlayerbotDefendPost(me.MapId, centre, 40f, 7);
                mateBrain.Defend = new PlayerbotDefendPost(me.MapId, centre, 40f, 7);
                strangerBrain.Defend = new PlayerbotDefendPost(me.MapId, centre, 40f, 8); // another post: not this healer's business
                mate.Player!.Health = 1;
                stranger.Player!.Health = 1;
                return true;
            });
            await host.WaitForWorldAsync(() => priest.Player!.VisibleObjects.Contains(mate.Player!.Guid) && priest.Player.VisibleObjects.Contains(stranger.Player!.Guid),
                "the post mates are in sight");
            await host.OnWorldAsync(() =>
            {
                priest.ManagedBudget = new ManagedActionBudget(4);
                healer.Update(500);
                return true;
            });
            await host.WaitForWorldAsync(() => mate.Player!.Health == mate.Player.MaxHealth, "the defender of the same post is healed to full");
            Assert.True(await host.OnWorldAsync(() => stranger.Player!.Health < stranger.Player.MaxHealth / 2));
        }
        finally
        {
            healer.Stop();
            mateBrain.Stop();
            strangerBrain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
            mate.Kick(); await mate.ManagedClosed;
            priest.Kick(); await priest.ManagedClosed;
        }
    }

    [Fact]
    public Task ClearDropsPvpDesired() => Run(25f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.NotEqual(0u, (uint)(bot.Flags & PlayerFlags.PvpDesired));
        rig.Brain.Defend = null;
        rig.Think();
        Assert.Equal(0u, (uint)(bot.Flags & PlayerFlags.PvpDesired));
    }));

    [Fact]
    public async Task DefendStateIsNotPersisted()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        Guid id = await PartyTestHost.StartBotAsync(host, "Defendkeep");
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Defendkeep")!;
            var post = new PlayerbotDefendPost(player.MapId, new Vector3(player.X, player.Y, player.Z), 40f, 3);
            Assert.True(Assert.Single(feature.SetDefend([id], post)).Success);
            Assert.Equal(post, feature.FindBrain(id)!.Defend);
            return true;
        });

        Assert.True((await feature.StopAsync(id.ToString())).Success);
        Assert.True((await feature.StartAsync(id.ToString())).Success);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Defendkeep") is { IsInWorld: true } && feature.FindBrain(id) is not null, "the bot runs again");
        Assert.Null(await host.OnWorldAsync(() => feature.FindBrain(id)!.Defend));
    }

    [Fact]
    public Task DefenderIgnoresAGameMasterInGmMode() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.EnemyPlayer.Flags |= PlayerFlags.Gm;
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Null(rig.Brain.InspectionPvpTarget);
        Assert.Null(rig.BotPlayer.Combat.Victim);
    }));

    [Fact]
    public Task DefenderOffThePostMapStillFightsAPlayerWhoAttacksIt() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        rig.Brain.Defend = new PlayerbotDefendPost(bot.MapId + 99, new Vector3(bot.X, bot.Y, bot.Z), 40f, 1);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);
    }));

    private static bool Desired(Player player) => (player.Flags & PlayerFlags.PvpDesired) != 0;

    [Fact]
    public async Task ClearAfterABrainReplacementTakesTheFlagOff()
    {
        await using WorldTestHost host = PartyTestHost.Start(o =>
        {
            o.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            o.Party.MasterTimeoutSeconds = 1;
        });
        Guid id = await PartyTestHost.StartBotAsync(host, "Defreplaced");
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Defreplaced")!;
            Assert.True(Assert.Single(feature.SetDefend([id], new PlayerbotDefendPost(player.MapId, new Vector3(player.X, player.Y, player.Z), 40f, 5))).Success);
            return true;
        });
        await host.WaitForWorldAsync(() => Desired(host.World.FindOnlinePlayer("Defreplaced")!), "the defender flags itself");

        // A party AI takes the bot and gives it back: the brain is replaced and keeps the post.
        WorldTestClient master = await host.EnterWorldAsync("DEFREPMASTER", "Defrepmaster");
        await master.SendAsync(ArcaneCore.Protocol.WorldOpcode.CmsgGroupInvite, PartyTestHost.CString("Defreplaced"));
        await host.WaitForWorldAsync(() => feature.IsPartyDriven(id), "the party AI drives the bot");
        await master.DisposeAsync();
        await host.WaitForWorldAsync(() => !feature.IsPartyDriven(id) && feature.FindBrain(id) is { Defend: not null }, "a fresh brain holds the post");

        await host.OnWorldAsync(() => Assert.Single(feature.SetDefend([id], null)).Success);
        Assert.False(await host.OnWorldAsync(() => Desired(host.World.FindOnlinePlayer("Defreplaced")!)));
        await Task.Delay(700);
        Assert.False(await host.OnWorldAsync(() => Desired(host.World.FindOnlinePlayer("Defreplaced")!)));
        await feature.ShutdownBeforeWorldStopAsync();
    }

    [Fact]
    public async Task StoppingADefenderLeavesNoPvpDesireToSave()
    {
        await using WorldTestHost host = PartyTestHost.Start();
        Guid id = await PartyTestHost.StartBotAsync(host, "Defstopped");
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(host);
        Player player = null!;
        await host.OnWorldAsync(() =>
        {
            player = host.World.FindOnlinePlayer("Defstopped")!;
            Assert.True(Assert.Single(feature.SetDefend([id], new PlayerbotDefendPost(player.MapId, new Vector3(player.X, player.Y, player.Z), 40f, 6))).Success);
            return true;
        });
        await host.WaitForWorldAsync(() => Desired(player), "the defender flags itself");

        Assert.True((await feature.StopAsync(id.ToString())).Success);

        // The player object outlives its session: whatever the character was saved with, the desire was already off.
        Assert.False(Desired(player));
    }

    private static Creature Spawn(WorldTestHost host, Player near, uint entry, float dx, float dy, string name)
    {
        var creature = new Creature(entry, new CreatureTemplate
        {
            Entry = entry, Name = name, CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500,
        }, null, CreatureContent.Empty, new Random((int)entry));
        creature.Relocate(near.X + dx, near.Y + dy, near.Z, 0, host.World.NowMs);
        near.Map!.AddObject(creature);
        return creature;
    }

    /// <summary>The guard is the players' friend (as a faction template would make it); everything else keeps the default rules.</summary>
    private sealed class GuardHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b)
            => base.IsFriendly(a, b) || (a is Player && b is Creature { Entry: GuardEntry }) || (b is Player && a is Creature { Entry: GuardEntry });
    }
}

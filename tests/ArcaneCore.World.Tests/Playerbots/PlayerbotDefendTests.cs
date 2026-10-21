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

    /// <summary>The friendly guard fights <paramref name="enemy"/> inside the post (the guard is in combat, the bot is not); the bot has no post yet.</summary>
    private static void GuardFights(Rig rig, Creature guard, Creature enemy)
    {
        Map map = rig.BotPlayer.Map!;
        Assert.True(map.Combat.Attack(enemy, guard));
        map.Combat.DealDamage(enemy, guard, 1);
        Assert.True(guard.Combat.IsInCombat);
        Assert.False(rig.BotPlayer.Combat.IsInCombat);
    }

    private static async Task<(Creature Guard, Creature Enemy)> SpawnFightersAsync(Rig rig, bool civilianEnemy, float enemyDz)
    {
        Creature guard = null!, enemy = null!;
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            bot.Map!.Combat.Hooks = new GuardHooks();
            guard = Spawn(rig.Host, bot, GuardEntry, 3f, 2f, "guard");
            enemy = Spawn(rig.Host, bot, RaiderEntry, 2f, 0f, "raider", civilianEnemy, enemyDz);
            return true;
        });
        await rig.Host.WaitForWorldAsync(() => rig.BotPlayer.VisibleObjects.Contains(guard.Guid) && rig.BotPlayer.VisibleObjects.Contains(enemy.Guid),
            "the guard and its enemy are in sight");
        return (guard, enemy);
    }

    [Fact]
    public Task DefenderDoesNotEngageACivilianTheGuardFights() => Run(60f, async rig =>
    {
        (Creature guard, Creature civilian) = await SpawnFightersAsync(rig, civilianEnemy: true, enemyDz: 0f);
        await rig.Host.OnWorldAsync(() =>
        {
            GuardFights(rig, guard, civilian);
            rig.Brain.Defend = rig.Post(40f);
            rig.Think();
            Assert.Null(rig.Brain.InspectionPvpTarget);
            Assert.Null(rig.BotPlayer.Combat.Victim);
        });
    });

    [Fact]
    public Task DefenderDoesNotEngageAHelpTargetOnAnotherLevel() => Run(60f, async rig =>
    {
        (Creature guard, Creature raider) = await SpawnFightersAsync(rig, civilianEnemy: false, enemyDz: 25f);
        await rig.Host.OnWorldAsync(() =>
        {
            GuardFights(rig, guard, raider);
            rig.Brain.Defend = rig.Post(40f);
            rig.Think();
            // 25 yards up is a chase the bot could not walk anyway, so the target alone proves nothing: the think must not be spent on it.
            Assert.NotEqual(PlayerbotGoalKind.Combat, rig.Brain.Goal);
            Assert.Null(rig.Brain.InspectionPvpTarget);
            Assert.Null(rig.BotPlayer.Combat.Victim);
        });
    });

    [Fact]
    public Task DefenderEngagesAHelpTargetOnItsOwnLevel_ControlForTheLevelAndCivilianSkips() => Run(60f, async rig =>
    {
        (Creature guard, Creature raider) = await SpawnFightersAsync(rig, civilianEnemy: false, enemyDz: 0f);
        await rig.Host.OnWorldAsync(() =>
        {
            GuardFights(rig, guard, raider);
            rig.Brain.Defend = rig.Post(40f);
            rig.Think();
            Assert.Same(raider, rig.Brain.InspectionPvpTarget);
        });
    });

    [Fact]
    public Task DefenderStillFightsACreatureOnAnotherLevelThatAttacksIt() => Run(60f, async rig =>
    {
        (Creature _, Creature raider) = await SpawnFightersAsync(rig, civilianEnemy: false, enemyDz: 25f);
        await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            Assert.True(bot.Map!.Combat.Attack(raider, bot));
            bot.Map!.Combat.DealDamage(raider, bot, 1);
            Assert.Contains(raider, bot.Combat.Attackers);
            rig.Brain.Defend = rig.Post(40f);
            rig.Think();
            // The think is spent on the attacker (the goal is Combat, aimed at the raider's entry), though 25 yards of height make it a chase the
            // bot cannot walk without ground (its target is dropped at once, as a refused walk does).
            Assert.Equal(PlayerbotGoalKind.Combat, rig.Brain.Goal);
            Assert.Equal(RaiderEntry, rig.Brain.TargetEntry);
        });
    });

    [Fact]
    public Task DefenderIgnoresAFlaggedEnemyPlayerOnAnotherLevel() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        Player bot = rig.BotPlayer;
        rig.EnemyPlayer.Relocate(bot.X + 4f, bot.Y, bot.Z + 25f, 0, rig.Host.World.NowMs);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        // 25 yards up is a chase the bot could not walk anyway, so the target alone proves nothing: the think must not be spent on it.
        Assert.NotEqual(PlayerbotGoalKind.Combat, rig.Brain.Goal);
        Assert.Null(rig.Brain.InspectionPvpTarget);
        Assert.Null(bot.Combat.Victim);
    }));

    [Fact]
    public Task DefenderEngagesAFlaggedEnemyPlayerOnItsOwnLevel_ControlForTheLevelSkip() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        Player bot = rig.BotPlayer;
        rig.EnemyPlayer.Relocate(bot.X + 4f, bot.Y, bot.Z + 5f, 0, rig.Host.World.NowMs);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);
    }));

    [Fact]
    public Task DefenderPassesOverAHostilePlayerItFoundNoPathTo_AtTheNextThink() => Run(25f, rig => rig.Host.OnWorldAsync(() =>
    {
        // 25 yards away and no ground loaded: the chase is refused. The first think tries it; the next ones do not choose it again.
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Equal(PlayerbotGoalKind.Combat, rig.Brain.Goal);
        Assert.Null(rig.Brain.InspectionPvpTarget);

        rig.Think();
        Assert.NotEqual(PlayerbotGoalKind.Combat, rig.Brain.Goal);
    }));

    [Fact]
    public Task DefenderFindsThreatenedByCreature_WhenACloserOneInThreatenedByIsSkipped() => Run(60f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        Map map = bot.Map!;
        Creature closerSkipped = Spawn(rig.Host, bot, RaiderEntry, 1f, 0f, "skipped-threat", civilian: true);
        Creature fartherValid = Spawn(rig.Host, bot, RaiderEntry + 1, 3f, 0f, "valid-threat", civilian: false);
        foreach (Creature c in new[] { closerSkipped, fartherValid })
        {
            Assert.True(map.Combat.Attack(c, bot));
            map.Combat.DealDamage(c, bot, 1);
            Assert.True(map.Combat.AttackStop(c));
            c.Combat.Threat.AddThreat(bot, 10f);
        }

        Assert.Empty(bot.Combat.Attackers);
        Assert.Contains(closerSkipped, bot.Combat.ThreatenedBy);
        Assert.Contains(fartherValid, bot.Combat.ThreatenedBy);
        Assert.Same(closerSkipped, rig.Brain.FindDefensiveAttacker(bot, defend: false));
        Assert.Same(fartherValid, rig.Brain.FindDefensiveAttacker(bot, defend: true));

        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Same(fartherValid, rig.Brain.InspectionPvpTarget);
        Assert.Same(fartherValid, bot.Combat.Victim);
    }));

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

    private static bool Moving(Player player) => (player.Movement.Flags & MovementFlags.MaskMoving) != 0;

    [Fact]
    public Task ADefenderWithResurrectionSickness_StaysWhereItWasRevived_UntilTheAuraEnds_ThenWalksBackToThePost() => Run(4f, async rig =>
    {
        await PartyTestHost.InstallFlatGroundAsync(rig.Host, await rig.Host.OnWorldAsync(() => rig.BotPlayer.Z));
        Vector3 graveyard = await rig.Host.OnWorldAsync(() =>
        {
            Player bot = rig.BotPlayer;
            rig.Brain.Defend = rig.Post(40f);
            // The spirit healer revived the bot 60 yards from its post (beyond 0.75 x the radius), sick.
            bot.Relocate(bot.X - 60f, bot.Y, bot.Z, 0, rig.Host.World.NowMs);
            PlayerbotSicknessTestKit.Apply(rig.Host.WorldServices, bot);
            Assert.True(PlayerbotSickness.IsSick(rig.Bot, bot));
            return new Vector3(bot.X, bot.Y, bot.Z);
        });

        // While the aura lasts: no walk back, however many thinks, and the status says why.
        for (int think = 0; think < 6; think++)
        {
            await rig.Host.OnWorldAsync(() =>
            {
                rig.Think();
                Player bot = rig.BotPlayer;
                Assert.False(Moving(bot), $"the sick defender walks at think {think}");
                Assert.Equal(graveyard.X, bot.X);
                Assert.Equal(graveyard.Y, bot.Y);
                // The wait is shown with the real risk decision, not instead of it.
                Assert.Equal("decision=defend-wait-sickness; decision=off", rig.Brain.RiskReport);
                Assert.True(rig.Brain.DefendWaitsSickness);
                Assert.Equal(PlayerbotGoalKind.Home, rig.Brain.Goal);
            });
        }

        // The aura ends: the defender walks back at once, and the status no longer says it waits.
        await rig.Host.OnWorldAsync(() =>
        {
            rig.Host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(rig.BotPlayer, PlayerbotSicknessTestKit.Sickness.Id);
            Assert.False(PlayerbotSickness.IsSick(rig.Bot, rig.BotPlayer));
            rig.Think();
            Assert.True(Moving(rig.BotPlayer), "the defender does not walk back once the aura is gone");
            Assert.False(rig.Brain.DefendWaitsSickness);
            Assert.DoesNotContain("defend-wait-sickness", rig.Brain.RiskReport, StringComparison.Ordinal);
        });
    });

    [Fact]
    public Task ADefenderWithResurrectionSickness_StillFightsAPlayerWhoAttacksIt() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        PlayerbotPvpTestKit.Flag(bot);
        PlayerbotPvpTestKit.Flag(rig.EnemyPlayer);
        PlayerbotSicknessTestKit.Apply(rig.Host.WorldServices, bot);
        Assert.True(bot.Map!.Combat.Attack(rig.EnemyPlayer, bot));
        bot.Map!.Combat.DealDamage(rig.EnemyPlayer, bot, 1);
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.Same(rig.EnemyPlayer, rig.Brain.InspectionPvpTarget);
        Assert.Same(rig.EnemyPlayer, bot.Combat.Victim);
    }));

    [Fact]
    public Task ASickDefenderWhoDies_NoLongerReportsTheWait_ButTheRealDecision() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        Player bot = rig.BotPlayer;
        rig.Brain.Defend = rig.Post(40f);
        PlayerbotSicknessTestKit.Apply(rig.Host.WorldServices, bot);
        rig.Think();
        Assert.True(rig.Brain.DefendWaitsSickness);
        Assert.StartsWith("decision=defend-wait-sickness", rig.Brain.RiskReport, StringComparison.Ordinal);

        bot.Health = 0;
        bot.Map!.Combat.KillPlayer(bot);
        Assert.False(bot.IsAlive);
        rig.Think();
        Assert.False(rig.Brain.DefendWaitsSickness);
        Assert.Equal("decision=off", rig.Brain.RiskReport);
    }));

    [Fact]
    public Task ADefenderWithoutTheAura_ReportsNoWait() => Run(4f, rig => rig.Host.OnWorldAsync(() =>
    {
        rig.Brain.Defend = rig.Post(40f);
        rig.Think();
        Assert.False(rig.Brain.DefendWaitsSickness);
        Assert.DoesNotContain("defend-wait-sickness", rig.Brain.RiskReport, StringComparison.Ordinal);
    }));

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

    private static Creature Spawn(WorldTestHost host, Player near, uint entry, float dx, float dy, string name, bool civilian = false, float dz = 0f)
    {
        var creature = new Creature(entry, new CreatureTemplate
        {
            Entry = entry, Name = name, CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 500, MaxLevelHealth = 500, Civilian = civilian,
        }, null, CreatureContent.Empty, new Random((int)entry));
        creature.Relocate(near.X + dx, near.Y + dy, near.Z + dz, 0, host.World.NowMs);
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

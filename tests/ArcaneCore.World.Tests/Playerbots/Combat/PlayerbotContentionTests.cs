using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Bots sharing the mobs of one place (docs/areas/playbots.md, Contention): soft claims spread bots over untagged mobs, a mob another
/// player tags is left alone (or helped with), a stolen kill is not waited on, and a quest creature that gives no credit is set aside.
/// </summary>
public sealed class PlayerbotContentionTests
{
    private static PlayerbotOptions Options(Action<PlayerbotContentionOptions>? configure = null, bool risk = false)
    {
        var options = new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500, Risk = { Enabled = risk } };
        configure?.Invoke(options.Contention);
        return options;
    }

    [Theory]
    [InlineData(false, 25f)]
    [InlineData(true, 25f)]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    public async Task TwoBotsAndTwoMobs_PickDifferentMobs_OnlyBecauseOfTheClaims(bool risk, float penalty)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession botA = await CombatTestSessions.EnterAsync(host, "CONTA", "Conta");
        WorldSession botB = await CombatTestSessions.EnterAsync(host, "CONTB", "Contb");
        var claims = new PlayerbotClaims(64);
        var brainA = new PlayerbotBrain(botA, Options(c => c.ClaimPenaltyYards = penalty, risk), claims: claims);
        var brainB = new PlayerbotBrain(botB, Options(c => c.ClaimPenaltyYards = penalty, risk), claims: claims);
        try
        {
            Creature one = null!, two = null!;
            await host.OnWorldAsync(() =>
            {
                one = AddCreature(host, botA.Player!, 995001, 5f); // the jitter (10 yd at most) never outweighs the 11 yd between the mobs: the offsets alone give both bots this one
                two = AddCreature(host, botA.Player!, 995002, 16f);
                return true;
            });
            await host.WaitForWorldAsync(() => botA.Player!.VisibleObjects.Contains(one.Guid) && botA.Player.VisibleObjects.Contains(two.Guid)
                && botB.Player!.VisibleObjects.Contains(one.Guid) && botB.Player.VisibleObjects.Contains(two.Guid), "both mobs seen by both bots");
            await host.OnWorldAsync(() =>
            {
                botA.ManagedBudget = new ManagedActionBudget(1);
                brainA.Update(500);
                botB.ManagedBudget = new ManagedActionBudget(1);
                brainB.Update(500);
                Assert.NotNull(brainA.InspectionTarget);
                Assert.NotNull(brainB.InspectionTarget);
                if (penalty > 0f) Assert.NotSame(brainA.InspectionTarget, brainB.InspectionTarget);
                else Assert.Same(brainA.InspectionTarget, brainB.InspectionTarget); // no claim weight: both take the nearest
                return true;
            });
        }
        finally
        {
            brainA.Stop(); brainB.Stop();
            botB.Kick(); await botB.ManagedClosed;
            botA.Kick(); await botA.ManagedClosed;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHeldMobAnotherPlayerTags_IsDroppedByDefault_AndTheBotMovesOn(bool risk)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "CONTBOT", "Contbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "CONTSTR", "Contstr");
        var brain = new PlayerbotBrain(bot, Options(risk: risk));
        try
        {
            Creature near = null!, far = null!;
            await host.OnWorldAsync(() =>
            {
                near = AddCreature(host, bot.Player!, 995011, 4f);
                far = AddCreature(host, bot.Player!, 995012, 14f);
                return true;
            });
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(near.Guid) && bot.Player.VisibleObjects.Contains(far.Guid), "mobs");
            Creature held = null!;
            await host.OnWorldAsync(() =>
            {
                bot.ManagedBudget = new ManagedActionBudget(1);
                NoSpells(bot);
                brain.Update(500);
                held = brain.InspectionTarget!;
                Assert.NotNull(held);
                // Another player tags the mob the bot holds.
                bot.Player!.Map!.Combat.DealDamage(stranger.Player!, held, 1);
                Think(bot, brain);
                Assert.NotSame(held, brain.InspectionTarget);
                Assert.True(brain.Contention.IsSetAside(held.Guid.Value, host.World.NowMs));
                Assert.False(brain.Contention.IsAssisting(held.Guid));
                return true;
            });
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALosingTagger_WithHelpChanceOne_IsAssisted_AndTheKillIsNotLooted(bool risk)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "CONTBOT", "Contbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "CONTSTR", "Contstr");
        var brain = new PlayerbotBrain(bot, Options(c => c.HelpChance = 1f, risk));
        brain.Contention.RollOverride = (_, _, _) => 0.0;
        try
        {
            Creature mob = null!;
            await host.OnWorldAsync(() => mob = AddCreature(host, bot.Player!, 995021, 4f));
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(mob.Guid), "mob");
            await host.OnWorldAsync(() =>
            {
                bot.ManagedBudget = new ManagedActionBudget(1);
                NoSpells(bot);
                brain.Update(500);
                Assert.Same(mob, brain.InspectionTarget);
                bot.Player!.Map!.Combat.DealDamage(stranger.Player!, mob, 1);
                stranger.Player!.Health = Math.Max(1u, stranger.Player!.MaxHealth / 4); // the tagger is losing
                Think(bot, brain);
                Assert.Equal(PlayerbotHelpDecision.Help, brain.Contention.LastDecision);
                Assert.True(brain.Contention.IsAssisting(mob.Guid));
                Assert.Same(mob, brain.InspectionTarget);

                // The tagger kills it: the bot gets nothing, so it asks for no loot and does not wait.
                bot.Player!.Map!.Combat.DealDamage(stranger.Player!, mob, mob.Health, direct: true, meleeDamage: true);
                Assert.False(mob.IsAlive);
                Think(bot, brain);
                Assert.NotEqual(PlayerbotGoalKind.Loot, brain.Goal);
                Assert.Null(brain.InspectionTarget);
                Assert.False(brain.Contention.IsAssisting(mob.Guid));
                return true;
            });
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Fact]
    public async Task AnUntaggedMobFightingAStranger_IsNeverHelped_EvenAtHelpChanceOne()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "CONTBOT", "Contbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "CONTSTR", "Contstr");
        var brain = new PlayerbotBrain(bot, Options(c => c.HelpChance = 1f));
        brain.Contention.RollOverride = (_, _, _) => 0.0;
        try
        {
            Creature mob = null!;
            await host.OnWorldAsync(() => mob = AddCreature(host, bot.Player!, 995041, 4f));
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(mob.Guid), "mob");
            await host.OnWorldAsync(() =>
            {
                bot.ManagedBudget = new ManagedActionBudget(1);
                NoSpells(bot);
                brain.Update(500);
                Assert.Same(mob, brain.InspectionTarget);
                // The stranger is on the mob's threat list and the mob fights it, but nobody holds the tap.
                mob.Combat.Threat.AddThreat(stranger.Player!, 100f);
                Assert.True(PlayerbotBrain.IsSomeoneElses(bot.Player!, mob, null)); // a fight of its own, not the bot's
                stranger.Player!.Health = Math.Max(1u, stranger.Player!.MaxHealth / 4);
                Assert.True(mob.LootTapper.IsEmpty);
                Think(bot, brain);
                Assert.NotEqual(PlayerbotHelpDecision.Help, brain.Contention.LastDecision);
                Assert.False(brain.Contention.IsAssisting(mob.Guid));
                return true;
            });
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    [Fact]
    public void ARealPlayerTagger_RaisesTheHelpChance()
    {
        double bot = PlayerbotContention.HelpProbability(0.35f, 0.5, taggerIsReal: false, sharesQuest: false);
        double real = PlayerbotContention.HelpProbability(0.35f, 0.5, taggerIsReal: true, sharesQuest: false);
        double shared = PlayerbotContention.HelpProbability(0.35f, 0.5, taggerIsReal: false, sharesQuest: true);
        Assert.Equal(0.35 * 0.5, bot, 6);
        Assert.Equal(bot * 2, real, 6);
        Assert.Equal(bot * 1.5, shared, 6);
        Assert.Equal(1.0, PlayerbotContention.HelpProbability(1f, 1.0, true, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStolenKill_AsksForNoLoot_AndDoesNotWaitTenSeconds(bool risk)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession bot = await CombatTestSessions.EnterAsync(host, "CONTBOT", "Contbot");
        WorldSession stranger = await CombatTestSessions.EnterAsync(host, "CONTSTR", "Contstr");
        var brain = new PlayerbotBrain(bot, Options(risk: risk));
        try
        {
            Creature mob = null!;
            await host.OnWorldAsync(() => mob = AddCreature(host, bot.Player!, 995031, 4f));
            await host.WaitForWorldAsync(() => bot.Player!.VisibleObjects.Contains(mob.Guid), "mob");
            await host.OnWorldAsync(() =>
            {
                bot.ManagedBudget = new ManagedActionBudget(1);
                NoSpells(bot);
                brain.Update(500);
                Assert.Same(mob, brain.InspectionTarget);
                bot.Player!.Map!.Combat.DealDamage(stranger.Player!, mob, mob.Health, direct: true, meleeDamage: true); // tags and kills
                Assert.False(mob.IsAlive);
                Assert.False(PlayerbotBrain.HasLootRights(bot.Player, mob));
                Think(bot, brain);
                Assert.NotEqual(PlayerbotGoalKind.Loot, brain.Goal);
                Assert.Null(brain.InspectionTarget);
                Assert.True(brain.Contention.IsSetAside(mob.Guid.Value, host.World.NowMs));
                return true;
            });
        }
        finally
        {
            brain.Stop();
            stranger.Kick(); await stranger.ManagedClosed;
            bot.Kick(); await bot.ManagedClosed;
        }
    }

    private const uint QuestEntry = 299;

    /// <summary>Contested hunting: one look per second at the spawns with every creature taken by others.</summary>
    private static uint Contest(PlayerbotContention contention, uint from, uint seconds)
    {
        uint now = from;
        for (uint i = 0; i < seconds; i++)
        {
            now += 1_000;
            contention.NoteContested(QuestEntry, now);
        }

        return now;
    }

    [Fact]
    public void ALongWalkOrTownTripWithoutCredit_NeverSuspendsTheEntry()
    {
        var contention = new PlayerbotContention(Options(c => c.ContentionSuspendMinutes = 5));
        Assert.False(contention.ObserveObjective(QuestEntry, 0, 1_000));
        // An hour of walking, resting, dying and turn-in walks: the objective is refreshed every second, nothing is contested.
        for (uint now = 2_000; now < 3_600_000; now += 1_000)
            Assert.False(contention.ObserveObjective(QuestEntry, 0, now));
        Assert.Equal(0u, contention.ContestedMs);
        Assert.False(contention.IsEntrySuspended(QuestEntry, 3_600_000));
    }

    [Fact]
    public void AnObservationGap_PausesTheContestedClock_InsteadOfCountingTheGap()
    {
        var contention = new PlayerbotContention(Options());
        Assert.False(contention.ObserveObjective(QuestEntry, 0, 1_000));
        uint now = Contest(contention, 1_000, 100); // 99 s contested (the first look only starts the clock)
        Assert.Equal(99_000u, contention.ContestedMs);
        now += 10 * 60_000; // died, ran back, shopped: ten minutes without a contested look
        now = Contest(contention, now, 80);
        Assert.Equal(99_000u + 79_000u, contention.ContestedMs); // the first look after the gap adds nothing
        Assert.False(contention.ObserveObjective(QuestEntry, 0, now));
        Assert.False(contention.IsEntrySuspended(QuestEntry, now));
    }

    [Fact]
    public void ThreeMinutesOfRealContention_SuspendTheEntry_ForTheConfiguredTime()
    {
        var contention = new PlayerbotContention(Options(c => c.ContentionSuspendMinutes = 5));
        Assert.False(contention.ObserveObjective(QuestEntry, 0, 1_000));
        uint now = Contest(contention, 1_000, 100);
        Assert.False(contention.ObserveObjective(QuestEntry, 0, now));
        now = Contest(contention, now, 82); // 99 + 82 s
        Assert.True(contention.ObserveObjective(QuestEntry, 0, now));
        Assert.True(contention.IsEntrySuspended(QuestEntry, now + (5 * 60_000) - 1));
        Assert.False(contention.IsEntrySuspended(QuestEntry, now + (5 * 60_000)));
    }

    [Fact]
    public void CreditOrADifferentObjective_RestartsTheContestedClock_AndLostMobsCount()
    {
        var contention = new PlayerbotContention(Options());
        Assert.False(contention.ObserveObjective(QuestEntry, 0, 1_000));
        uint now = Contest(contention, 1_000, 150);
        Assert.False(contention.ObserveObjective(QuestEntry, 1, now)); // credit
        Assert.Equal(0u, contention.ContestedMs);
        for (int i = 0; i < 8; i++) contention.NoteLostMob(QuestEntry); // eight mobs stolen or left to others
        Assert.False(contention.ObserveObjective(QuestEntry, 1, now + 1_000));
        contention.NoteLostMob(QuestEntry);
        Assert.True(contention.ObserveObjective(QuestEntry, 1, now + 2_000));
        Assert.False(contention.ObserveObjective(QuestEntry + 1, 0, now + 3_000)); // another objective starts clean
        Assert.Equal(0u, contention.ContestedMs);
    }

    [Fact]
    public void ClaimsCountOtherBotsOnly_AndLapse()
    {
        var claims = new PlayerbotClaims(16);
        uint first = claims.Join(77, 1, 100);
        uint second = claims.Join(77, 2, 200);
        Assert.Equal(first, second);
        Assert.Equal(1, claims.OthersOf(77, mine: true, 300));
        Assert.Equal(2, claims.OthersOf(77, mine: false, 300));
        claims.Leave(77, first);
        Assert.Equal(1, claims.ClaimantsOf(77, 300));
        Assert.Equal(0, claims.OthersOf(77, mine: false, 200 + PlayerbotClaims.ClaimLifeMs));
    }

    [Fact]
    public void DropIndexNamesTheCreaturesThatDropAnItem_AndCachesPerContent()
    {
        const uint wolf = 299, meat = 750, other = 12;
        var loot = new LootContent([(LootTableKind.Creature, new LootStoreRow(500, meat, 50f, 0, 1, 1))],
            [new CreatureLootInfo(wolf, 500, 0, 0, 0)]);
        Assert.Equal([wolf], PlayerbotDropIndex.DroppersOf(loot, meat));
        Assert.Empty(PlayerbotDropIndex.DroppersOf(loot, other));
    }

    /// <summary>A few thinks (the first ones may be spent acknowledging movement orders).</summary>
    private static void NoSpells(WorldSession session)
        => session.Services.GetRequiredService<SpellFeature>().System.Store = new SpellStore([], [], []);

    private static void Think(WorldSession session, PlayerbotBrain brain)
    {
        for (int i = 0; i < 4; i++)
        {
            session.ManagedBudget = new ManagedActionBudget(1);
            brain.Update(500);
        }
    }

    private static Creature AddCreature(WorldTestHost host, Player near, uint low, float offset)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = low, Name = "contention candidate", CreatureType = 1,
            MinLevel = 1, MaxLevel = 1, MinLevelHealth = 50, MaxLevelHealth = 50,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(near.X + offset, near.Y, near.Z, 0, host.World.NowMs);
        near.Map!.AddObject(creature);
        return creature;
    }
}

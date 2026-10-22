using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Bot QA cluster 5, death loops in starter zones and repeat deaths to known dangers: creatures two to four levels above a low-level
/// bot (#501, #654, #704, #748, #888), the danger memory that did not keep the bot out of a fight with a creature it had fled from
/// (#788, bot-risk/died-in-known-hazard), and the run, rest, pull the same kind, run again loop (#816, bot-risk/retreat-loop).
/// </summary>
public sealed class PlayerbotDeathLoopRiskTests
{
    private static readonly PlayerbotRiskOptions Defaults = new();

    private static RiskEnemy Mob(RiskJoin join = RiskJoin.Target, byte level = 10, uint health = 400, float dps = 7f, uint entry = 100)
        => new(entry, level, 0, health, dps, join);

    private static PlayerbotEngagementFacts Bot(params RiskEnemy[] enemies) => new()
    {
        BotLevel = 10, BotHealth = 300, BotMaxHealth = 300, BotDps = PlayerbotRiskModel.PriorBotDps(10), Enemies = enemies, LootValue = 0.1f,
    };

    // --- level above the bot -----------------------------------------------------------------------------------------

    /// <summary>vmangos Unit::MagicSpellHitResult (player against creature: 96 - diff below 3, then 11 a level) and the crushing blow.</summary>
    [Fact]
    public void TheHitAndCrushFactors_FollowTheVanillaTables()
    {
        Assert.Equal(1f, PlayerbotRiskModel.HitFactor(0));
        Assert.Equal(1f, PlayerbotRiskModel.HitFactor(-5));
        Assert.Equal(94f / 96f, PlayerbotRiskModel.HitFactor(2), 4);
        Assert.Equal(83f / 96f, PlayerbotRiskModel.HitFactor(3), 4);
        Assert.Equal(72f / 96f, PlayerbotRiskModel.HitFactor(4), 4);
        Assert.Equal(1f / 96f, PlayerbotRiskModel.HitFactor(20), 4);

        Assert.Equal(1f, PlayerbotRiskModel.CrushFactor(3));
        Assert.Equal(1.125f, PlayerbotRiskModel.CrushFactor(4), 4); // skill 20 above: 25% crushing at 150%
        Assert.Equal(1.175f, PlayerbotRiskModel.CrushFactor(5), 4);
    }

    /// <summary>
    /// A level 6 bot (140 health, 12 damage a second from its fights with creatures of its level) and a quest creature of 200 health
    /// hitting for 6.72 a second: at the bot's level the estimate takes 16.7 seconds and 80% of its health, inside a quest's bar (0.9);
    /// three levels above, a sixth of its blows miss (17% against 4%), the fight takes 19.3 seconds and 92%, and is not pulled. Before,
    /// the level made no difference (bot QA #704, #748: level 6 bots dying to level 9 creatures).
    /// </summary>
    [Fact]
    public void AQuestCreatureThreeLevelsAbove_IsHarderThanItsTemplateSays_AndIsNotPulledAtTheEdge()
    {
        PlayerbotEngagementFacts Facts(byte level) => new()
        {
            BotLevel = 6, BotHealth = 140, BotMaxHealth = 140, BotDps = 12f, QuestObjective = true,
            Enemies = [new RiskEnemy(200, level, 0, 200, 6.72f, RiskJoin.Target)],
        };

        PlayerbotEngagement even = PlayerbotRiskModel.Assess(Facts(6), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Engage, even.Decision);
        Assert.InRange(even.Risk, 0.79f, 0.81f);

        PlayerbotEngagement above = PlayerbotRiskModel.Assess(Facts(9), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, above.Decision);
        Assert.Equal("too-strong", above.Reason);
        Assert.InRange(above.Risk, 0.91f, 0.94f);

        // The hazard scan's strength test says the same: a creature above the bot is beyond it, one of its level is not.
        RiskEnemy creature = new(200, 9, 0, 200, 6.72f, RiskJoin.Target);
        Assert.True(PlayerbotRiskModel.BeyondStrengthAlone(6, 140, 12f, creature, Defaults));
        Assert.False(PlayerbotRiskModel.BeyondStrengthAlone(6, 140, 12f, creature with { Level = 6 }, Defaults));
    }

    // --- the danger memory over the whole fight ---------------------------------------------------------------------

    /// <summary>
    /// A creature the bot fled from or died to answers the call of the creature it would pull (or stands in reach of where it would
    /// fight): the fight is the remembered one again, whichever creature the bot targets. Before, only the target was looked up and the
    /// bot took the fight ("ok-with-1-adds"), and died to the creature its memory held (bot QA #788).
    /// </summary>
    [Fact]
    public void ARememberedCreatureThatWouldJoinTheFight_KeepsTheBotOffIt()
    {
        RiskEnemy helper = Mob(RiskJoin.Assist, health: 50, dps: 2f, entry: 101);
        Assert.Equal(PlayerbotEngageDecision.Engage, PlayerbotRiskModel.Assess(Bot(Mob(), helper), Defaults).Decision);

        PlayerbotEngagement assist = PlayerbotRiskModel.Assess(Bot(Mob(), helper with { Remembered = true }), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, assist.Decision);
        Assert.Equal("remembered", assist.Reason);

        PlayerbotEngagement spot = PlayerbotRiskModel.Assess(Bot(Mob(), helper with { Join = RiskJoin.FightSpot, Remembered = true }), Defaults);
        Assert.Equal("remembered", spot.Reason);

        // Not a reason to look for a group: it is the memory, not the strength.
        Assert.Equal(0, PlayerbotRiskModel.NeededGroupSize(Bot(Mob(), helper with { Remembered = true }) with { QuestObjective = true }, Defaults, 5));
    }

    /// <summary>A remembered creature only along the way: the fight is fine, the straight walk through its reach is not.</summary>
    [Fact]
    public void ARememberedCreatureOnlyAlongTheWay_IsWalkedRound()
    {
        RiskEnemy onPath = Mob(RiskJoin.Path, health: 50, dps: 2f, entry: 101);
        Assert.Equal(PlayerbotEngageDecision.Engage, PlayerbotRiskModel.Assess(Bot(Mob(), onPath), Defaults).Decision);

        PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(Bot(Mob(), onPath with { Remembered = true }), Defaults);
        Assert.Equal(PlayerbotEngageDecision.Detour, verdict.Decision);
        Assert.Equal("path-adds-1", verdict.Reason);
    }

    // --- a kind that beat the bot -----------------------------------------------------------------------------------

    /// <summary>
    /// A kind that beat the bot one against one is not pulled alone again while the memory lasts, quest or not: the estimate let one
    /// through before and the fight said otherwise (bot QA #816, three retreats begun inside two minutes, reason 'losing'). A group is
    /// weighed as before, so a quest objective becomes a "needs a group" goal instead.
    /// </summary>
    [Fact]
    public void AKindThatBeatTheBotAlone_IsNotPulledAloneAgain_AGroupStillIs()
    {
        PlayerbotEngagementFacts quest = Bot(Mob()) with { QuestObjective = true };
        Assert.Equal(PlayerbotEngageDecision.Engage, PlayerbotRiskModel.Assess(quest, Defaults).Decision);

        PlayerbotEngagement lost = PlayerbotRiskModel.Assess(quest with { LostToKind = true }, Defaults);
        Assert.Equal(PlayerbotEngageDecision.Avoid, lost.Decision);
        Assert.Equal("lost-to-kind", lost.Reason);

        Assert.Equal(PlayerbotEngageDecision.Engage, PlayerbotRiskModel.Assess(quest with { LostToKind = true, GroupSize = 2 }, Defaults).Decision);
        Assert.Equal(2, PlayerbotRiskModel.NeededGroupSize(quest with { LostToKind = true }, Defaults, 5));
    }

    [Fact]
    public void TheDangerMemory_KeepsKindsLostAlone_AndWhenEachCreatureWasFirstRemembered()
    {
        var memory = new PlayerbotDangerMemory();
        ObjectGuid one = new(0xF130000000000001UL), two = new(0xF130000000000002UL), three = new(0xF130000000000003UL);
        memory.Remember(0, Vector3.Zero, [(one, 7)], 1_000, 60, alone: true);
        memory.Remember(0, Vector3.Zero, [(two, 8), (three, 9)], 2_000, 60);

        Assert.True(memory.LostAlone(7, 2_000));
        Assert.False(memory.LostAlone(8, 2_000)); // one of two: the pack beat it, not the kind
        Assert.False(memory.LostAlone(7, 61_001)); // forgotten with the rest

        // Remembered again later, it keeps the time it was first remembered while still held.
        memory.Remember(0, Vector3.Zero, [(one, 7)], 5_000, 60);
        Assert.True(memory.RememberedBefore(one, 1_500, 5_000));
        Assert.False(memory.RememberedBefore(two, 1_500, 5_000)); // remembered after the fight began
        Assert.True(memory.RememberedBefore(two, 0, 5_000));
    }

    // --- on the world -----------------------------------------------------------------------------------------------

    private static CreatureTemplate Feeble(uint entry, uint health = 60) => RiskTestWorld.Template(entry, health: health) with
    {
        MinMeleeDamage = 1, MaxMeleeDamage = 1, MeleeBaseAttackTime = 600_000,
    };

    /// <summary>
    /// A retreat from one creature ends (it got away): another of its kind, which the bot would have pulled for its quest, is now passed
    /// over ("lost-to-kind"). After a retreat from two, a lone one of that kind is still pulled: the pack beat it, not the kind.
    /// </summary>
    [Fact]
    public async Task ARetreatFromOneCreature_PassesOverItsKind_ARetreatFromTwoDoesNot()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate lone = Feeble(992001), pair = Feeble(992002);
        (Creature fled, Creature other, Creature[] pack, Creature single) = await world.OnWorldAsync(() => (
            world.Spawn(lone, 0, 25), world.Spawn(lone, 0, -25),
            new[] { world.Spawn(pair, 25, 0), world.Spawn(pair, 27, 2) }, world.Spawn(pair, -25, 0)));
        await world.SeeAsync([fled, other, .. pack, single]);

        await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Player player = world.Player;
            Assert.Equal(PlayerbotEngageDecision.Engage, risk.Assess(player, other, false, out _).Decision);
            Assert.Equal(PlayerbotEngageDecision.Engage, risk.Assess(player, single, false, out _).Decision);

            // Nothing attacks the bot: each retreat is over (got away) at its first look.
            risk.StartRetreat(player, [fled], "losing");
            Assert.False(risk.UpdateRetreat(player, RiskTestWorld.ThinkMs));
            Assert.Equal("safe", risk.Retreat.Outcome);
            PlayerbotEngagement afterLone = risk.Assess(player, other, false, out _);
            Assert.Equal(PlayerbotEngageDecision.Avoid, afterLone.Decision);
            Assert.Equal("lost-to-kind", afterLone.Reason);
            // As a quest objective it becomes a "needs a group" goal rather than a lone pull.
            PlayerbotEngagement quest = risk.Assess(player, other, true, out _);
            Assert.Equal(PlayerbotEngageDecision.Avoid, quest.Decision);
            Assert.Equal("group-of-2", quest.Reason);

            risk.StartRetreat(player, pack, "losing-to-2");
            Assert.False(risk.UpdateRetreat(player, RiskTestWorld.ThinkMs));
            Assert.NotEqual("lost-to-kind", risk.Assess(player, single, false, out _).Reason);
            return true;
        });
    }

    /// <summary>
    /// The bot would pull a lone-looking creature whose neighbour, which it fled from, answers its call: the world's facts mark the
    /// neighbour remembered, and the pull is passed over.
    /// </summary>
    [Fact]
    public async Task APullThatWouldBringARememberedNeighbour_IsPassedOver()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        (Creature target, Creature neighbour) = await world.OnWorldAsync(() =>
            (world.Spawn(Feeble(992011), 25, 0), world.Spawn(Feeble(992012, health: 10), 28, 2)));
        await world.SeeAsync(target, neighbour);

        await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            PlayerbotEngagement before = risk.Assess(world.Player, target, false, out _);
            Assert.Equal(PlayerbotEngageDecision.Engage, before.Decision);
            Assert.Equal(1, before.Adds); // the neighbour is part of the fight

            risk.Memory.Remember(world.Player.MapId, new Vector3(neighbour.X, neighbour.Y, neighbour.Z), [(neighbour.Guid, neighbour.Entry)],
                world.Host.World.NowMs, world.Options.Risk.DangerMemorySeconds);
            Assert.False(risk.IsRemembered(target));
            PlayerbotEngagement after = risk.Assess(world.Player, target, true, out _); // a fresh key: no cached verdict
            Assert.Equal(PlayerbotEngageDecision.Avoid, after.Decision);
            Assert.Equal("remembered", after.Reason);
            return true;
        });
    }

    /// <summary>
    /// A retreat that cannot shake its pursuer off ends cornered and the bot fights on; the retreat remembers the creature as it ends. A
    /// death in that same fight is not a death to a known danger: the memory is the fight's own. Live 2026-10-16, 509 of 1218
    /// bot-risk/died-in-known-hazard reports came within two minutes of the same bot's bot-risk/retreat-cornered (Pikdoul: cornered at
    /// 07:28:46.9, died to creature 3123 at 07:28:48.0).
    /// </summary>
    [Fact]
    public async Task ADeathInTheFightACorneredRetreatRemembered_IsNotADeathToAKnownDanger()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature creature = await world.OnWorldAsync(() => world.Spawn(Feeble(992021, health: 5000), 3, 0));
        await world.SeeAsync(creature);
        using AnomalyCapture capture = await world.OnWorldAsync(() => BotAnomalyCapture.For(world.Player));

        await world.OnWorldAsync(() =>
        {
            world.Engage(creature);
            world.Brain.Risk.Track(world.Player); // the fight begins
            world.Brain.Risk.StartRetreat(world.Player, [creature], "losing");
            return true;
        });
        await world.Host.World.AdvanceClockAsync(PlayerbotRetreat.MaxMs + 1_000);
        await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Assert.True(world.Player.IsAlive);
            Assert.False(risk.UpdateRetreat(world.Player, RiskTestWorld.ThinkMs));
            Assert.Equal("cornered", risk.Retreat.Outcome);
            Assert.True(risk.IsRemembered(creature)); // the retreat remembered it as it ended

            world.Player.Health = 0;
            world.Player.Map!.Combat.KillPlayer(world.Player);
            risk.OnDeath(world.Player, [(creature.Guid, creature.Entry)]);
            return true;
        });

        Assert.True(capture.Any("bot-risk/retreat-cornered"), "the retreat did not end cornered: " + string.Join("; ", capture.Notices.Select(n => n.Key)));
        Assert.False(capture.Any("bot-risk/died-in-known-hazard"), "the cornered fight's own memory was reported as older knowledge");
    }

    /// <summary>Positive control: a creature remembered before the fight began, which then kills the bot, is reported.</summary>
    [Fact]
    public async Task ADeathToACreatureRememberedBeforeTheFight_IsADeathToAKnownDanger()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature creature = await world.OnWorldAsync(() => world.Spawn(Feeble(992031, health: 5000), 3, 0));
        await world.SeeAsync(creature);
        using AnomalyCapture capture = await world.OnWorldAsync(() => BotAnomalyCapture.For(world.Player));

        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.Memory.Remember(world.Player.MapId, new Vector3(creature.X, creature.Y, creature.Z), [(creature.Guid, creature.Entry)],
                world.Host.World.NowMs, world.Options.Risk.DangerMemorySeconds);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
        await world.OnWorldAsync(() =>
        {
            world.Engage(creature);
            world.Brain.Risk.Track(world.Player);
            world.Player.Health = 0;
            world.Player.Map!.Combat.KillPlayer(world.Player);
            world.Brain.Risk.OnDeath(world.Player, [(creature.Guid, creature.Entry)]);
            return true;
        });

        Assert.True(capture.Any("bot-risk/died-in-known-hazard"));
    }

    [Fact]
    public async Task ChooseTarget_SkipsCreatureCoveredByActiveFledOrRetreatHazard_AndSecondRetreatInCamp_AddsCampHazard()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature c1 = await world.OnWorldAsync(() => world.Spawn(Feeble(992041, health: 4000), 20, 0));
        Creature c2 = await world.OnWorldAsync(() => world.Spawn(Feeble(992042, health: 4000), 35, 0));
        Creature c3 = await world.OnWorldAsync(() => world.Spawn(Feeble(992043, health: 4000), 60, 0));
        await world.SeeAsync(c1, c2, c3);

        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;

            // 1. Retreat from two creatures at +20 yd. Neither kind is remembered alone,
            //    but a non-remembered neighbour standing inside c1's active "fled" hazard is passed over.
            float startX = player.X, startY = player.Y;
            Creature nearLair = world.Spawn(Feeble(992044, health: 4000), 25, 0);
            player.VisibleObjects.Add(nearLair.Guid);
            player.SetPosition(startX + 20f, startY, player.Z, player.Orientation);
            risk.StartRetreat(player, [c1, c2], "losing");
            Assert.False(risk.UpdateRetreat(player, RiskTestWorld.ThinkMs));
            player.SetPosition(startX, startY, player.Z, player.Orientation);

            Creature? chosenNearRetreat = risk.ChooseTarget(player, 0u,
                creature => !ReferenceEquals(creature, nearLair), questObjective: false);
            Assert.Null(chosenNearRetreat);

            // 2. Second retreat within RetreatLoopWatch.WindowMs and RetreatCampYards (at +35 yd)
            //    records an 80-yard camp hazard so a 3rd pull in the same camp (c3 at +60 yd) is refused.
            player.SetPosition(startX + 35f, startY, player.Z, player.Orientation);
            risk.StartRetreat(player, [c1, c2], "losing");
            Assert.False(risk.UpdateRetreat(player, RiskTestWorld.ThinkMs));
            player.SetPosition(startX, startY, player.Z, player.Orientation);

            Assert.True(risk.Hazards.CoversDeathLoop(player.MapId, new Vector3(c3.X, c3.Y, c3.Z), world.Host.World.NowMs));
            Creature? chosenInCamp = risk.ChooseTarget(player, 0u,
                creature => !ReferenceEquals(creature, c3), questObjective: false);
            Assert.Null(chosenInCamp);
            return true;
        });
    }

    [Fact]
    public async Task DyingAtCorpseReclaimSpot_OrToMidFightScannedHazard_DoesNotReportKnownHazard()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        using var cap = BotAnomalyCapture.For(world.Player);
        Creature killer = await world.OnWorldAsync(() => world.Spawn(Feeble(9910, health: 400), 5, 0));
        await world.SeeAsync(killer);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;

            // 1. A prior death records a "death" place mark and killer entry at the corpse spot.
            risk.OnDeath(player, [(killer.Guid, killer.Entry)]);
            Assert.True(risk.IsRemembered(killer));
            return true;
        });

        await world.Host.World.AdvanceClockAsync(5_000);
        await world.OnWorldAsync(() =>
        {
            // 2. Dying again right at the corpse spot after reclaiming the body (or after Track runs while dead)
            //    does not falsely report died-in-known-hazard.
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            world.Engage(killer);
            risk.Track(player);
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            risk.Track(player); // Track while dead must not clear _fightSinceMs before OnDeath
            risk.OnDeath(player, [(killer.Guid, killer.Entry)]);
            Assert.False(cap.Any("bot-risk/died-in-known-hazard"), cap.Describe());
            return true;
        });
    }
}

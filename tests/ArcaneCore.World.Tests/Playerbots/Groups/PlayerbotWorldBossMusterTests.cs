using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.WorldBossRealRows;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The pure muster rules of a world-boss raid (<see cref="PlayerbotWorldBossMuster"/>, p7b design 3.5): the level floor (the real level-63 bosses
/// against the level-60 player cap), the nearest-candidate caller chosen before any roll, the synthetic (boss, day, team) seed the willingness
/// rolls use, the size and the raid role quotas. The rolls are <see cref="PlayerbotRecruitment.Roll"/> as the recruiting step uses them.
/// </summary>
public sealed class PlayerbotWorldBossMusterTests
{
    private const uint Alliance = 1, Horde = 2;
    private const long UtcDay = 20_000;
    private static readonly Vector3 BossAt = new(1000f, 2000f, 100f);

    private static PlayerbotWorldBossEntry BossOf(uint entry = Azuregos, byte level = 63, int? sizeOverride = null)
        => new(entry, "Test Boss", level, PlayerbotWorldBossLocator.Spawn(1, 52349, BossAt), sizeOverride, Derived: false);

    private static readonly PlayerbotWorldBossOptions Defaults = new();
    private static readonly PlayerbotGroupOptions Groups = new();

    private static PlayerbotRecruitFacts WillingFacts(PlayerbotGroupCandidate caller, PlayerbotGroupCandidate candidate)
        => new(Sociability: 1f, Affinity: 100, WorstAffinity: 100, Rival: false, FillsRole: false, Guildmate: false, AnsweredCall: false);

    private static PlayerbotRecruitFacts RivalFacts(PlayerbotGroupCandidate caller, PlayerbotGroupCandidate candidate)
        => new(1f, 100, 100, Rival: true, false, false, false);

    private static int SeedOf(uint boss = Azuregos, uint team = Alliance, long day = UtcDay) => PlayerbotWorldBossMuster.Seed(boss, day, team);

    /// <summary>Guids whose roll for the seed is below the best chance (0.95) of the willing facts above: they accept.</summary>
    private static List<ulong> AcceptingGuids(int seed, int count)
    {
        var found = new List<ulong>();
        for (ulong guid = 1; found.Count < count; guid++)
            if (PlayerbotRecruitment.Roll(guid, seed) < PlayerbotRecruitment.MaxChance - 0.05) found.Add(guid);
        return found;
    }

    /// <summary>A guid whose roll for the seed is at or above the 0.95 ceiling: it refuses however willing it is.</summary>
    private static ulong RefusingGuid(int seed)
    {
        for (ulong guid = 1; ; guid++)
            if (PlayerbotRecruitment.Roll(guid, seed) >= PlayerbotRecruitment.MaxChance) return guid;
    }

    private static PlayerbotGroupCandidate Bot(ulong guid, Class cls, byte level = 60, uint team = Alliance, uint map = 1, float distance = 100f, uint need = 0)
        => new(new Guid((int)guid, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), guid, "Bot" + guid, cls, level, team, map, BossAt + new Vector3(distance, 0f, 0f),
            cls == Class.Warrior ? PlayerbotRole.Tank : cls == Class.Priest ? PlayerbotRole.Healer : PlayerbotRole.RangeDps, 1f, need);

    /// <summary>A pool of accepting bots: the given counts of warriors (tanks), priests (healers) and mages (damage), each nearer the boss than the last.</summary>
    private static List<PlayerbotGroupCandidate> Pool(int seed, int tanks, int healers, int damage, byte level = 60)
    {
        List<ulong> guids = AcceptingGuids(seed, tanks + healers + damage);
        var pool = new List<PlayerbotGroupCandidate>();
        int i = 0;
        for (int t = 0; t < tanks; t++, i++) pool.Add(Bot(guids[i], Class.Warrior, level, distance: 10f + i));
        for (int h = 0; h < healers; h++, i++) pool.Add(Bot(guids[i], Class.Priest, level, distance: 10f + i));
        for (int d = 0; d < damage; d++, i++) pool.Add(Bot(guids[i], Class.Mage, level, distance: 10f + i));
        return pool;
    }

    // --- the level floor ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFloor_IsTheLowerOfBossLevelAndPlayerCap_MinusTheGap()
    {
        Assert.Equal((byte)58, PlayerbotWorldBossMuster.LevelFloor(Defaults, 63));
        Assert.Equal((byte)58, PlayerbotWorldBossMuster.LevelFloor(Defaults, 60));
        Assert.Equal((byte)57, PlayerbotWorldBossMuster.LevelFloor(Defaults, 59));
        Assert.Equal((byte)60, PlayerbotWorldBossMuster.LevelFloor(new PlayerbotWorldBossOptions { LevelGap = 0 }, 63));
        Assert.Equal((byte)40, PlayerbotWorldBossMuster.LevelFloor(new PlayerbotWorldBossOptions { LevelGap = 20 }, 63));
    }

    /// <summary>The real levels: Azuregos, Kazzak and the four dragons are 63 and Teremus is 60 in the world database; players stop at 60 (PlayerbotPopulationOptions.MaxLevel).</summary>
    [Theory]
    [InlineData(Azuregos, 63)]
    [InlineData(Kazzak, 63)]
    [InlineData(Ysondre, 63)]
    [InlineData(Lethon, 63)]
    [InlineData(Emeriss, 63)]
    [InlineData(Taerar, 63)]
    [InlineData(Teremus, 60)]
    public void EveryRealBoss_HasFloor58_SoALevel60BotQualifiesForALevel63Boss(uint entry, int realLevel)
    {
        Row row = Rank3ContinentSpawns.Concat(Dragons).Single(r => r.Entry == entry);
        Assert.Equal((realLevel, realLevel), ((int)row.MinLevel, (int)row.MaxLevel));
        byte floor = PlayerbotWorldBossMuster.LevelFloor(Defaults, row.MaxLevel);
        Assert.Equal((byte)58, floor);
        Assert.True(60 >= floor);
        Assert.True(58 >= floor);
        Assert.False(57 >= floor);
    }

    [Fact]
    public void ThePlan_TakesLevel58AndUp_AndLeavesLevel57Out()
    {
        int seed = SeedOf();
        List<PlayerbotGroupCandidate> pool = Pool(seed, tanks: 4, healers: 10, damage: 16);
        List<ulong> extra = AcceptingGuids(seed, 60);
        var low = Bot(extra[50], Class.Mage, level: 57, distance: 1f);          // nearest of all, one level under the floor
        var edge = Bot(extra[51], Class.Mage, level: 58, distance: 2f);
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, [.. pool, low, edge], WillingFacts);

        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, result.Verdict);
        Assert.DoesNotContain(result.Plan!.Members, m => m.Bot.BotId == low.BotId);
        Assert.Equal(edge.BotId, result.Caller!.BotId); // the nearest eligible candidate: the level-57 bot nearer still is not a candidate
    }

    // --- the seed --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSeed_IsTheDocumentedMixOfBossDayAndTeam_StableAndDistinct()
    {
        int expected = (int)PlayerbotContention.Mix(PlayerbotContention.Mix(Azuregos, (ulong)UtcDay), Alliance);
        Assert.Equal(expected, SeedOf());
        Assert.Equal(SeedOf(), SeedOf());
        Assert.NotEqual(SeedOf(), SeedOf(day: UtcDay + 1));
        Assert.NotEqual(SeedOf(), SeedOf(team: Horde));
        Assert.NotEqual(SeedOf(), SeedOf(boss: Kazzak));
    }

    [Fact]
    public void TheWillingnessRoll_IsFixedForTheDay_SoARefusalHoldsAndAnotherDayRollsAgain()
    {
        int today = SeedOf();
        ulong refuser = RefusingGuid(today);
        var o = new PlayerbotWorldBossOptions { MinRaidSize = 5, DefaultRaidSize = 6 };
        PlayerbotGroupCandidate caller = Bot(900_001, Class.Warrior, distance: 1f);
        // The shy bot is the nearest after the caller, so only its refusal (not the order) keeps it out of a raid of six.
        PlayerbotGroupCandidate shy = Bot(refuser, Class.Mage, distance: 2f);
        List<PlayerbotGroupCandidate> others = Pool(today, 0, 2, 3);
        PlayerbotGroupCandidate[] all = [caller, shy, .. others];

        PlayerbotWorldBossMusterResult first = PlayerbotWorldBossMuster.Plan(o, Groups, BossOf(), Alliance, UtcDay, all, WillingFacts);
        PlayerbotWorldBossMusterResult again = PlayerbotWorldBossMuster.Plan(o, Groups, BossOf(), Alliance, UtcDay, all, WillingFacts);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, first.Verdict);
        Assert.DoesNotContain(first.Plan!.Members, m => m.Bot.BotId == shy.BotId);
        Assert.Equal(first.Plan.Members.Select(m => m.Bot.BotId).Order(), again.Plan!.Members.Select(m => m.Bot.BotId).Order());

        // On a later day the same bot rolls again (a hash of bot and day): find one where it comes, and it then takes the nearer seat.
        long day = UtcDay + 1;
        while (PlayerbotRecruitment.Roll(refuser, SeedOf(day: day)) >= PlayerbotRecruitment.MaxChance - 0.05) day++;
        List<PlayerbotGroupCandidate> othersThatDay = Pool(SeedOf(day: day), 0, 2, 3);
        PlayerbotWorldBossMusterResult later = PlayerbotWorldBossMuster.Plan(o, Groups, BossOf(), Alliance, day, [caller, shy, .. othersThatDay], WillingFacts);
        Assert.Contains(later.Plan!.Members, m => m.Bot.BotId == shy.BotId);
    }

    // --- the caller ------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCaller_IsTheNearestCandidateToTheBoss_OnTies_TheLowerBotId()
    {
        PlayerbotGroupCandidate far = Bot(1, Class.Warrior, distance: 400f), near = Bot(2, Class.Mage, distance: 50f), alsoNear = Bot(3, Class.Mage, distance: 50f);
        Assert.Equal(near.BotId, PlayerbotWorldBossMuster.Caller([far, near], BossAt)!.BotId);
        PlayerbotGroupCandidate lower = near.BotId.CompareTo(alsoNear.BotId) < 0 ? near : alsoNear;
        Assert.Equal(lower.BotId, PlayerbotWorldBossMuster.Caller([far, near, alsoNear], BossAt)!.BotId);
        Assert.Equal(lower.BotId, PlayerbotWorldBossMuster.Caller([alsoNear, near, far], BossAt)!.BotId); // not the list order
        Assert.Null(PlayerbotWorldBossMuster.Caller([], BossAt));
    }

    [Fact]
    public void TheCaller_IsChosenBeforeAnyRoll_AndLeadsEvenIfItWouldHaveRefused()
    {
        int seed = SeedOf();
        ulong refuser = RefusingGuid(seed);
        PlayerbotGroupCandidate caller = Bot(refuser, Class.Mage, distance: 5f); // nearest, and its own roll is a refusal
        List<PlayerbotGroupCandidate> pool = Pool(seed, 4, 10, 16);
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, [caller, .. pool], WillingFacts);

        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, result.Verdict);
        Assert.Equal(caller.BotId, result.Caller!.BotId);
        Assert.Equal(caller.BotId, result.Plan!.Leader.BotId);
        Assert.Contains(result.Plan.Members, m => m.Bot.BotId == caller.BotId);
    }

    [Fact]
    public void EveryoneRefusing_LeavesOnlyTheCaller_SoTheMusterIsTooSmall()
    {
        List<PlayerbotGroupCandidate> pool = Pool(SeedOf(), 4, 10, 26);
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, pool, RivalFacts);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.TooFew, result.Verdict);
        Assert.NotNull(result.Caller);
        Assert.Null(result.Plan);
    }

    // --- who is a candidate -----------------------------------------------------------------------------------------------

    [Fact]
    public void OnlyBotsOfTheTeam_OnTheBossMap_WithinTheMusterRadius_AreAsked()
    {
        int seed = SeedOf();
        List<PlayerbotGroupCandidate> pool = Pool(seed, 4, 10, 26);
        List<ulong> extra = AcceptingGuids(seed, 80);
        PlayerbotGroupCandidate[] strangers =
        [
            Bot(extra[60], Class.Warrior, team: Horde, distance: 1f),
            Bot(extra[61], Class.Priest, map: 0, distance: 1f),
            Bot(extra[62], Class.Mage, distance: 3001f),   // 3000 yards is the default radius
            Bot(extra[63], Class.Mage, distance: 20_000f),
        ];
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, [.. strangers, .. pool], WillingFacts);

        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, result.Verdict);
        Assert.All(strangers, s => Assert.DoesNotContain(result.Plan!.Members, m => m.Bot.BotId == s.BotId));
        Assert.Equal(pool.Select(p => p.BotId).Order(), result.Plan!.Members.Select(m => m.Bot.BotId).Order());
    }

    [Fact]
    public void ABotJustInsideTheRadius_IsAsked_AndTheRadiusIsLive()
    {
        int seed = SeedOf();
        List<PlayerbotGroupCandidate> pool = Pool(seed, 4, 10, 25);
        PlayerbotGroupCandidate far = Bot(AcceptingGuids(seed, 90)[85], Class.Mage, distance: 2999f);
        PlayerbotWorldBossMusterResult wide = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, [.. pool, far], WillingFacts);
        Assert.Contains(wide.Plan!.Members, m => m.Bot.BotId == far.BotId);

        PlayerbotWorldBossMusterResult narrow = PlayerbotWorldBossMuster.Plan(new PlayerbotWorldBossOptions { MusterRadiusYards = 1000f }, Groups, BossOf(), Alliance, UtcDay, [.. pool, far], WillingFacts);
        Assert.NotNull(narrow.Plan);
        Assert.DoesNotContain(narrow.Plan.Members, m => m.Bot.BotId == far.BotId);
    }

    [Fact]
    public void NoCandidates_IsNoMuster()
        => Assert.Equal(PlayerbotWorldBossMusterVerdict.NoCandidates, PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, [], WillingFacts).Verdict);

    // --- the size --------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSize_IsTheTargetWhenEnoughAccept_AndTheCooperationRaidCapBoundsIt()
    {
        List<PlayerbotGroupCandidate> pool = Pool(SeedOf(), 6, 14, 40); // 60 accepting bots
        PlayerbotWorldBossMusterResult full = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, pool, WillingFacts);
        Assert.Equal(40, full.Size);
        Assert.Equal(40, full.Plan!.Members.Count);

        var smaller = new PlayerbotGroupOptions { Cooperation = { MaxRaidSize = 30 } };
        PlayerbotWorldBossMusterResult capped = PlayerbotWorldBossMuster.Plan(Defaults, smaller, BossOf(), Alliance, UtcDay, pool, WillingFacts);
        Assert.Equal(30, capped.Size);
        Assert.Equal(30, capped.Plan!.Members.Count);
    }

    [Fact]
    public void ASizeOverride_ReplacesTheDefaultTarget()
    {
        List<PlayerbotGroupCandidate> pool = Pool(SeedOf(), 6, 14, 40);
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(sizeOverride: 32), Alliance, UtcDay, pool, WillingFacts);
        Assert.Equal(32, result.Size);
        Assert.Equal(32, result.Plan!.Members.Count);
    }

    [Fact]
    public void WhenFewerAcceptThanTheTarget_TheRaidIsAsBigAsAccepted_ButNotBelowTheMinimum()
    {
        int seed = SeedOf();
        PlayerbotWorldBossMusterResult thirtyThree = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(seed, 5, 10, 18), WillingFacts);
        Assert.Equal(33, thirtyThree.Size);

        PlayerbotWorldBossMusterResult thirty = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(seed, 4, 9, 17), WillingFacts);
        Assert.Equal(30, thirty.Size);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, thirty.Verdict);

        PlayerbotWorldBossMusterResult twentyNine = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(seed, 4, 9, 16), WillingFacts);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.TooFew, twentyNine.Verdict);
        Assert.Null(twentyNine.Plan);
    }

    [Fact]
    public void AMinimumAndTargetOfFive_AllowsASmallTestRaid()
    {
        var o = new PlayerbotWorldBossOptions { MinRaidSize = 5, DefaultRaidSize = 6 };
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(o, Groups, BossOf(), Alliance, UtcDay, Pool(SeedOf(), 2, 3, 6), WillingFacts);
        Assert.Equal(6, result.Size);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, result.Verdict);
    }

    [Fact]
    public void WithRaidEscalationOff_TheSignalLimitIsAParty_SoNoWorldBossRaidForms()
    {
        var party = new PlayerbotGroupOptions { Cooperation = { RaidEscalation = false } };
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, party, BossOf(), Alliance, UtcDay, Pool(SeedOf(), 6, 14, 40), WillingFacts);
        Assert.Equal(PlayerbotGroupContent.PartySize, PlayerbotRecruitment.SignalLimit(party));
        Assert.Equal(PlayerbotWorldBossMusterVerdict.TooFew, result.Verdict);
    }

    // --- the raid quotas -------------------------------------------------------------------------------------------------

    [Fact]
    public void AFortyRaid_HoldsFourTanksAndTenHealers_AtLeast()
    {
        // RunRoleNeeds(40, raid): ceil(40 * 1 / 10) = 4 tanks, ceil(40 * 25 / 100) = 10 healers.
        Assert.Equal((4, 10), PlayerbotGroupContent.RunRoleNeeds(40, raid: true, Groups));
        PlayerbotWorldBossMusterResult result = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(SeedOf(), 4, 10, 30), WillingFacts);

        Assert.Equal(PlayerbotWorldBossMusterVerdict.Formed, result.Verdict);
        Assert.Equal(40, result.Plan!.Members.Count);
        Assert.True(result.Plan.Members.Count(m => m.Role == PlayerbotGroupRole.Tank) >= 4);
        Assert.True(result.Plan.Members.Count(m => m.Role == PlayerbotGroupRole.Healer) >= 10);
    }

    [Fact]
    public void WithoutEnoughHealersOrTanks_TheRaidDoesNotForm_AndStaysOpen()
    {
        int seed = SeedOf();
        PlayerbotWorldBossMusterResult noHealers = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(seed, 6, 0, 40), WillingFacts);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.Roles, noHealers.Verdict);
        Assert.Null(noHealers.Plan);
        Assert.NotNull(noHealers.Caller);

        PlayerbotWorldBossMusterResult twoTanks = PlayerbotWorldBossMuster.Plan(Defaults, Groups, BossOf(), Alliance, UtcDay, Pool(seed, 2, 12, 26), WillingFacts);
        Assert.Equal(PlayerbotWorldBossMusterVerdict.Roles, twoTanks.Verdict);
    }
}

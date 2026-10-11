using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots.Population;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Population;

/// <summary>Which running bots may be parked and in which order (<see cref="PopulationParking.Order"/>): the "least useful bot" choice.</summary>
public sealed class PopulationParkingTests
{
    private static readonly PopulationBand[] Bands =
    [
        new() { MinLevel = 1, MaxLevel = 10, Weight = 1 },
        new() { MinLevel = 11, MaxLevel = 20, Weight = 1 },
    ];

    private static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static PopulationBotFacts Bot(int n, byte level, bool autonomous = true, bool busy = false, bool playerInZone = false, bool vetoed = false)
        => new(Id(n), level, Team.Alliance, 12, autonomous, busy, playerInZone, vetoed);

    private static IReadOnlySet<Guid> Owned(params int[] ids) => ids.Select(Id).ToHashSet();

    private static Guid[] Order(IReadOnlyCollection<PopulationBotFacts> running, IReadOnlySet<Guid> owned, int[] desired, int[] have,
        IReadOnlyDictionary<Guid, long>? startedAt = null, long nowMs = 0, long minRunMs = 0, bool keepRecent = false)
        => [.. Picks(running, owned, desired, have, startedAt, nowMs, minRunMs, keepRecent).Select(p => p.Bot.BotId)];

    private static IReadOnlyList<PopulationParkPick> Picks(IReadOnlyCollection<PopulationBotFacts> running, IReadOnlySet<Guid> owned, int[] desired,
        int[] have, IReadOnlyDictionary<Guid, long>? startedAt = null, long nowMs = 0, long minRunMs = 0, bool keepRecent = false)
        => PopulationParking.Order(running, owned, Bands, desired, have, startedAt ?? new Dictionary<Guid, long>(), nowMs, minRunMs, keepRecent);

    [Fact]
    public void ABotTheOperatorMade_IsNeverChosen()
    {
        PopulationBotFacts[] running = [Bot(1, 5), Bot(2, 5), Bot(3, 5)];
        Assert.Equal([Id(2)], Order(running, Owned(2), [0, 0], [3, 0]));
        Assert.Empty(Order(running, Owned(), [0, 0], [3, 0]));
    }

    [Fact]
    public void ABotAPlayersPartyOrAScriptDrives_IsNeverChosen()
    {
        // Autonomous is false for a bot in a real player's party, a scripted bot and one a bot-led group drives.
        PopulationBotFacts[] running = [Bot(1, 5, autonomous: false), Bot(2, 5)];
        Assert.Equal([Id(2)], Order(running, Owned(1, 2), [0, 0], [2, 0]));
    }

    [Fact]
    public void ABusyBot_IsNeverChosen()
    {
        // Busy: in combat, teleporting or not in the world (a dead bot is not busy: see the productivity tests).
        PopulationBotFacts[] running = [Bot(1, 5, busy: true), Bot(2, 5)];
        Assert.Equal([Id(2)], Order(running, Owned(1, 2), [0, 0], [2, 0]));
    }

    [Fact]
    public void TheBandWithTheLargestSurplus_GivesTheFirstBot_AndABotInNoBandGoesBeforeAll()
    {
        PopulationBotFacts[] running = [Bot(1, 5), Bot(2, 15), Bot(3, 50)];
        // Band 0 wants 0 and has 1 (surplus 1); band 1 wants 0 and has 1 (surplus 1); level 50 is in no band.
        Assert.Equal(Id(3), Order(running, Owned(1, 2, 3), [0, 0], [1, 1])[0]);
        // Band 1 has the larger surplus.
        Assert.Equal(Id(2), Order([Bot(1, 5), Bot(2, 15)], Owned(1, 2), [1, 0], [1, 1])[0]);
    }

    [Fact]
    public void WithinABand_ABotWithNoPlayerInItsZone_GoesBeforeOneThatHasCompany()
    {
        PopulationBotFacts[] running = [Bot(1, 5, playerInZone: true), Bot(2, 5, playerInZone: false)];
        Assert.Equal([Id(2), Id(1)], Order(running, Owned(1, 2), [0, 0], [2, 0]));
    }

    [Fact]
    public void WithinThatTheBotThatHasRunLongest_GoesFirst_ThenTheLowerBotId()
    {
        // A rotation: the bot started longest ago has had its turn. (Before 2026-10-11 the most recently started went first, so a
        // restarted bot was the next one parked: 1,687 starts for 269 bots in six hours.)
        PopulationBotFacts[] running = [Bot(1, 5), Bot(2, 5), Bot(3, 5), Bot(4, 5)];
        var startedAt = new Dictionary<Guid, long> { [Id(1)] = 10, [Id(2)] = 30, [Id(3)] = 20 };
        // Bot 4 was never started by this process (0: before a restart, or by an operator), so it has run longest of the four.
        Assert.Equal([Id(4), Id(1), Id(3), Id(2)], Order(running, Owned(1, 2, 3, 4), [0, 0], [4, 0], startedAt));
        // A tie on every key falls to the BotId.
        Assert.Equal([Id(1), Id(2)], Order([Bot(2, 5), Bot(1, 5)], Owned(1, 2), [0, 0], [2, 0]));
    }

    [Fact]
    public void TheSurplusIsRecountedAfterEachPick_SoNoBandIsDrainedBelowItsShareWhileAnotherHasASurplus()
    {
        // Band 0 wants 1 and has 3 (surplus 2), band 1 wants 1 and has 2 (surplus 1). Bands 0 holds bots with a player nearby, band 1 none.
        PopulationBotFacts[] running =
        [
            Bot(1, 5, playerInZone: true), Bot(2, 5, playerInZone: true), Bot(3, 5, playerInZone: true),
            Bot(4, 15), Bot(5, 15),
        ];
        var startedAt = new Dictionary<Guid, long> { [Id(1)] = 1, [Id(2)] = 2, [Id(3)] = 3, [Id(4)] = 1, [Id(5)] = 2 };
        // 1 (surplus 2, started first) -> counts 2/2 -> surpluses 1 and 1: the tie goes to the band with no player (4) -> counts 2/1
        // -> band 0 again (2) -> counts 1/1 -> surpluses 0 and 0: no player first (5), then the last one (3).
        Assert.Equal([Id(1), Id(4), Id(2), Id(5), Id(3)], Order(running, Owned(1, 2, 3, 4, 5), [1, 1], [3, 2], startedAt));
    }

    [Fact]
    public void TheLeastProductiveBotIsParkedFirst_DeadThenSickThenStalled_WhateverTheBandFullness()
    {
        // Band 1 (11-20) is the fullest (surplus 3) and the healthy bot there has no player near and has run longest: by band fullness
        // alone it would go first (the rule before 2026-10-11). A dead bot, a resurrection-sick one and a stalled one earn less.
        PopulationBotFacts[] running =
        [
            Bot(1, 15),
            Bot(2, 5, playerInZone: true) with { Stalled = true },
            Bot(3, 5, playerInZone: true) with { Sick = true },
            Bot(4, 5, playerInZone: true) with { Dead = true },
        ];
        var startedAt = new Dictionary<Guid, long> { [Id(2)] = 50, [Id(3)] = 50, [Id(4)] = 50 };
        IReadOnlyList<PopulationParkPick> picks = Picks(running, Owned(1, 2, 3, 4), [3, 0], [3, 3], startedAt);
        Assert.Equal([Id(4), Id(3), Id(2), Id(1)], picks.Select(p => p.Bot.BotId));
        Assert.Equal(["dead", "sick", "stalled", "surplus"], picks.Select(p => p.Reason));
    }

    [Fact]
    public void ADeadBotOrGhost_MayBeParked_ButNotOneInCombatOrTeleporting()
    {
        // Busy (in combat, teleporting, not in the world) keeps a bot; dead alone does not.
        PopulationBotFacts[] running = [Bot(1, 5, busy: true) with { Dead = true }, Bot(2, 5) with { Dead = true }];
        Assert.Equal([Id(2)], Order(running, Owned(1, 2), [0, 0], [2, 0]));
    }

    [Fact]
    public void AmongHealthyBots_TheBandWithTheLargerSurplusStillGoesFirst_AndTheReasonSaysRotationWhenNoBandIsOverItsShare()
    {
        PopulationBotFacts[] running = [Bot(1, 5), Bot(2, 15)];
        IReadOnlyList<PopulationParkPick> picks = Picks(running, Owned(1, 2), [5, 5], [1, 3]);
        // Band 0 is 4 short of its share, band 1 only 2: band 1 is nearer its surplus, so its bot goes first. Neither is over its share.
        Assert.Equal([Id(2), Id(1)], picks.Select(p => p.Bot.BotId));
        Assert.Equal(["rotation", "rotation"], picks.Select(p => p.Reason));
    }

    [Fact]
    public void AntiChurn_ABotStartedLessThanTheMinimumRunTimeAgo_IsKeptForTheBudget_AndParkedLastOverTheTarget()
    {
        PopulationBotFacts[] running = [Bot(1, 5) with { Dead = true }, Bot(2, 5), Bot(3, 5)];
        // Bot 1 (dead) and bot 2 were started 60 s ago, bot 3 ten minutes ago; the minimum run time is five minutes.
        var startedAt = new Dictionary<Guid, long> { [Id(1)] = 540_000, [Id(2)] = 540_000, [Id(3)] = 0 };
        Assert.Equal([Id(3)], Order(running, Owned(1, 2, 3), [0, 0], [3, 0], startedAt, nowMs: 600_000, minRunMs: 300_000, keepRecent: true));
        Assert.Equal([Id(3), Id(1), Id(2)], Order(running, Owned(1, 2, 3), [0, 0], [3, 0], startedAt, nowMs: 600_000, minRunMs: 300_000, keepRecent: false));
        // Once the minimum has passed, the dead bot goes first again.
        Assert.Equal([Id(1), Id(3), Id(2)], Order(running, Owned(1, 2, 3), [0, 0], [3, 0], startedAt, nowMs: 840_000, minRunMs: 300_000, keepRecent: true));
    }

    [Fact]
    public void TheRotation_RestartsTheBotParkedLongestAgoFirst_WhateverItsBand()
    {
        PlayerbotPopulationMember Member(int n, long updatedUnix) => new(Id(n), 100 + n, 12, 0, 1, updatedUnix);
        // Bot 3 was parked first (t=1000), bot 1 last (t=3000); bot 4 was never parked by this process (stopped before a restart), so
        // it has waited longest; bot 2 too, and its row is older than bot 4's.
        var parkedAt = new Dictionary<Guid, long> { [Id(1)] = 3000, [Id(3)] = 1000 };
        PlayerbotPopulationMember[] parked = [Member(1, 10), Member(2, 5), Member(3, 50), Member(4, 7)];
        Assert.Equal([Id(2), Id(4), Id(3), Id(1)], PopulationParking.RestartOrder(parked, parkedAt).Select(m => m.BotId));
    }

    [Fact]
    public void NothingRunning_OrdersNothing_AndTheInputCountsAreNotChanged()
    {
        Assert.Empty(Order([], Owned(1), [1, 1], [0, 0]));
        int[] have = [3, 2];
        Order([Bot(1, 5), Bot(2, 5), Bot(3, 5)], Owned(1, 2, 3), [0, 0], have);
        Assert.Equal([3, 2], have);
    }
}

using ArcaneCore.Game.Entities;
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
        IReadOnlyDictionary<Guid, long>? startedAt = null)
        => [.. PopulationParking.Order(running, owned, Bands, desired, have, startedAt ?? new Dictionary<Guid, long>()).Select(f => f.BotId)];

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
        // Busy: dead, in combat, teleporting or not in the world.
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
    public void WithinThatTheMostRecentlyStartedBot_GoesFirst_ThenTheLowerBotId()
    {
        PopulationBotFacts[] running = [Bot(1, 5), Bot(2, 5), Bot(3, 5), Bot(4, 5)];
        var startedAt = new Dictionary<Guid, long> { [Id(1)] = 10, [Id(2)] = 30, [Id(3)] = 20 };
        // Bot 4 was never recorded (0), so it comes last of the four.
        Assert.Equal([Id(2), Id(3), Id(1), Id(4)], Order(running, Owned(1, 2, 3, 4), [0, 0], [4, 0], startedAt));
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
        // 3 (surplus 2) -> counts 2/2 -> surpluses 1 and 1: the tie goes to the band with no player (5) -> counts 2/1 -> band 0 again (2)
        // -> counts 1/1 -> surpluses 0 and 0: no player first (4), then the last one (1).
        Assert.Equal([Id(3), Id(5), Id(2), Id(4), Id(1)], Order(running, Owned(1, 2, 3, 4, 5), [1, 1], [3, 2], startedAt));
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

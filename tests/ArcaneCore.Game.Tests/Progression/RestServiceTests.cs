using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Progression;

/// <summary>
/// Rested experience: accrual while resting in a capital city or an inn, the cap at one and a half levels, kill XP doubled from the
/// pool, offline accrual from the stored second, and the state a logout keeps. A level-1 player has 400 XP to the next level, so
/// 8 hours of resting (28800 s) are worth 400 * 28800 / 1152000 = 10 points of pool (mangos Player::ComputeRest).
/// </summary>
public sealed class RestServiceTests
{
    private const uint EightHoursMs = 8 * 3600 * 1000;

    private static readonly AreaTemplate Capital = new(1519, 0, 0, 1, (uint)AreaFlags.Capital, 0, "Stormwind City", 2, 0);
    private static readonly AreaTemplate Wilderness = new(12, 0, 0, 2, 0, 0, "Elwynn Forest", 2, 0);
    private static readonly AreaTriggerTemplate InnTrigger = new(1, 0, 0, 0, 83.5f, 10, 0, 0, 0, 0, "Inn");

    private static (Player Player, PlayerProgression Progression, RestService Service, FakeSession Session) Create(RestOptions? options = null, byte level = 1)
    {
        (Player player, FakeSession session) = PlayerProgressionTests.Create(level);
        PlayerProgression progression = PlayerProgressionTests.Progression();
        progression.InitializeLoadedPlayer(player);
        return (player, progression, new RestService(progression, options ?? new RestOptions()), session);
    }

    private sealed class Environment(bool outdoors = true, params AreaTriggerTemplate[] triggers) : IRestEnvironment
    {
        public bool Outdoors { get; set; } = outdoors;

        public AreaTriggerTemplate? FindAreaTrigger(uint triggerId) => triggers.FirstOrDefault(t => t.Id == triggerId);

        public bool IsOutdoors(Player player) => Outdoors;
    }

    [Fact]
    public void ComputeRest_EightHoursAreFivePercentOfTheLevelHalvedForTheClientDoubling()
    {
        Assert.Equal(10f, RestService.ComputeRest(400, 8 * 3600), 3);
        Assert.Equal(0f, RestService.ComputeRest(400, 0));
        Assert.Equal(0f, RestService.ComputeRest(400, -5));
        Assert.Equal(0f, RestService.ComputeRest(0, 3600)); // the max-level player has no next-level XP
    }

    [Fact]
    public void RestingInACity_AddsPoolOnlyAfterTenSeconds_AndPublishesTheRestedState()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        rest.SetRestType(player, RestType.InCity, 0, nowMs: 1000);
        Assert.True(RestService.IsResting(player));
        Assert.Equal(1, rest.RestingCount);

        rest.Update(1000 + 9_999, new Environment());
        Assert.Equal(0f, progression.RestBonus(player)); // vmangos: the update is frozen until 10 s have passed

        rest.Update(1000 + 10_000, new Environment());
        Assert.Equal(10 * 400 / 1152000f, progression.RestBonus(player), 6);

        // One long gap is worth the same as the sum of its parts: 8 hours from the last gain.
        rest.Update(1000 + 10_000 + EightHoursMs, new Environment());
        Assert.Equal(10f + (10 * 400 / 1152000f), progression.RestBonus(player), 3);
        Assert.Equal(PlayerProgression.RestStateRested, player.GetByte(UpdateFields.PlayerBytes2, 3));
        Assert.Equal(10u, player.GetUInt32(UpdateFields.PlayerRestStateExperience));
    }

    [Fact]
    public void RestingEveryTenSeconds_GivesTheSamePoolAsOneLongRest()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        rest.SetRestType(player, RestType.InTavern, 7, nowMs: 0);
        var environment = new Environment(outdoors: false);
        for (uint now = 10_000; now <= EightHoursMs; now += 10_000)
        {
            rest.Update(now, environment);
        }

        Assert.Equal(10f, progression.RestBonus(player), 1);
    }

    [Fact]
    public void ThePool_IsCappedAtOneAndAHalfLevels_AndTheClientIsToldTheCap()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        rest.SetRestType(player, RestType.InCity, 0, nowMs: 0);

        // 400 XP to the next level: the pool stops at 400 * 1.5 / 2 = 300 (the client shows double: 1.5 levels of the bar).
        rest.Update(uint.MaxValue / 2, new Environment()); // ~24 days
        Assert.Equal(300f, progression.RestBonus(player));
        Assert.Equal(300u, player.GetUInt32(UpdateFields.PlayerRestStateExperience));
        Assert.Equal(1.5f * 400, progression.RestBonus(player) * 2);
    }

    [Fact]
    public void NotResting_GainsNothing_AndLeavingTheRestStopsTheGain()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        rest.Update(EightHoursMs, new Environment());
        Assert.Equal(0f, progression.RestBonus(player));

        rest.SetRestType(player, RestType.InCity, 0, nowMs: 0);
        rest.SetRestType(player, RestType.None, 0, nowMs: 5_000);
        Assert.False(RestService.IsResting(player));
        Assert.Equal(0, rest.RestingCount);
        rest.Update(EightHoursMs, new Environment());
        Assert.Equal(0f, progression.RestBonus(player));
        Assert.Equal(RestType.None, rest.GetRestType(player));
    }

    [Fact]
    public void RateInGame_ScalesTheGain_AndZeroOrGarbageTurnsItOff()
    {
        foreach ((float rate, float expected) in new[] { (2f, 20f), (0.5f, 5f), (0f, 0f), (-3f, 0f), (float.NaN, 0f), (float.PositiveInfinity, 0f) })
        {
            (Player player, PlayerProgression progression, RestService rest, _) = Create(new RestOptions { RateInGame = rate });
            rest.SetRestType(player, RestType.InCity, 0, nowMs: 0);
            rest.Update(EightHoursMs, new Environment());
            Assert.Equal(expected, progression.RestBonus(player), 2);
        }
    }

    [Fact]
    public void KillXp_IsDoubledFromThePool_AndNonKillXpIsNot()
    {
        (Player player, PlayerProgression progression, RestService rest, FakeSession session) = Create();
        progression.SetRestBonus(player, 80);
        rest.SetRestType(player, RestType.InCity, 0, nowMs: 0);
        rest.Update(EightHoursMs, new Environment()); // +10 while resting
        Assert.Equal(90f, progression.RestBonus(player), 2);
        session.Clear();

        // A kill worth 50 XP takes 50 from the pool and gives 100 in all.
        Assert.Equal(100u, progression.GiveXp(player, 50, new ObjectGuid(0xF130_0000_0000_0001)));
        Assert.Equal(40f, progression.RestBonus(player), 2);
        Assert.Equal(100u, PlayerProgression.CurrentXp(player));
        var reader = new PacketReader(session.Next().Payload);
        reader.ReadUInt64();
        Assert.Equal(100u, reader.ReadUInt32()); // total, including the rested part
        Assert.Equal(0, reader.ReadByte()); // a kill
        Assert.Equal(50u, reader.ReadUInt32()); // without the rested part

        // A quest gives its XP as it is, and leaves the pool alone.
        progression.GiveXp(player, 20);
        Assert.Equal(120u, PlayerProgression.CurrentXp(player));
        Assert.Equal(40f, progression.RestBonus(player), 2);
    }

    [Fact]
    public void EnteringACapital_StartsACityRest_AndAnyOtherZoneEndsIt()
    {
        (Player player, _, RestService rest, _) = Create();
        rest.OnZoneEntered(player, Wilderness, 0);
        Assert.False(RestService.IsResting(player));

        rest.OnZoneEntered(player, Capital, 100);
        Assert.Equal(RestType.InCity, rest.GetRestType(player));
        Assert.True(RestService.IsResting(player));

        rest.OnZoneEntered(player, Wilderness, 200);
        Assert.Equal(RestType.None, rest.GetRestType(player));
        Assert.False(RestService.IsResting(player));

        // No area entry (client-zone mode): not a capital.
        rest.OnZoneEntered(player, Capital, 300);
        rest.OnZoneEntered(player, null, 400);
        Assert.False(RestService.IsResting(player));
    }

    [Fact]
    public void ATavernRest_SurvivesAZoneChange_ButACityOverridesIt_AndIsNotOverwrittenByATavernTrigger()
    {
        (Player player, _, RestService rest, _) = Create();
        rest.OnTavernTrigger(player, 1, 0);
        Assert.Equal(RestType.InTavern, rest.GetRestType(player));
        Assert.Equal(1u, rest.TavernTrigger(player));

        rest.OnZoneEntered(player, Wilderness, 10); // the tavern leave is checked by position, not by the zone
        Assert.Equal(RestType.InTavern, rest.GetRestType(player));

        rest.OnZoneEntered(player, Capital, 20); // a city overrides the inn
        Assert.Equal(RestType.InCity, rest.GetRestType(player));
        rest.OnTavernTrigger(player, 1, 30); // and an inn does not override a city
        Assert.Equal(RestType.InCity, rest.GetRestType(player));
    }

    [Fact]
    public void ATavernRest_EndsOutdoorsOutsideItsTrigger_OnlyAfterTheCheckInterval()
    {
        (Player player, _, RestService rest, _) = Create();
        var environment = new Environment(outdoors: true, InnTrigger);
        rest.OnTavernTrigger(player, 1, 0);

        // Inside the sphere (radius 10 around the origin): stays, outdoors or not.
        rest.Update(1_000, environment);
        Assert.Equal(RestType.InTavern, rest.GetRestType(player));

        player.SetPosition(500, 500, 83.5f, 0); // far outside
        rest.Update(1_500, environment); // before the next 1 s check
        Assert.Equal(RestType.InTavern, rest.GetRestType(player));

        environment.Outdoors = false; // an inn is indoors: a player in a building never leaves
        rest.Update(3_000, environment);
        Assert.Equal(RestType.InTavern, rest.GetRestType(player));

        environment.Outdoors = true;
        rest.Update(4_000, environment);
        Assert.Equal(RestType.None, rest.GetRestType(player));
        Assert.False(RestService.IsResting(player));
        Assert.Equal(0, rest.RestingCount);
    }

    [Fact]
    public void ATavernRest_EndsWhenItsTriggerIsGone()
    {
        (Player player, _, RestService rest, _) = Create();
        rest.OnTavernTrigger(player, 99, 0); // not in the environment (reloaded away)
        rest.Update(1_000, new Environment(outdoors: true, InnTrigger));
        Assert.Equal(RestType.None, rest.GetRestType(player));
    }

    [Fact]
    public void ACityRest_IsNeverEndedByThePositionCheck()
    {
        (Player player, _, RestService rest, _) = Create();
        rest.SetRestType(player, RestType.InCity, 0, 0);
        player.SetPosition(500, 500, 83.5f, 0);
        rest.Update(5_000, new Environment(outdoors: true, InnTrigger));
        Assert.Equal(RestType.InCity, rest.GetRestType(player));
    }

    [Fact]
    public void TheAccrualClockSurvivesTheMillisecondCounterWrapping()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        uint start = uint.MaxValue - 4_999;
        rest.SetRestType(player, RestType.InCity, 0, start);
        rest.Update(unchecked(start + 10_000), new Environment()); // wrapped past zero
        Assert.Equal(10 * 400 / 1152000f, progression.RestBonus(player), 6);
    }

    [Fact]
    public void Login_AddsOfflineRest_AtTheRateOfThePlaceTheCharacterLoggedOutIn()
    {
        const long logout = 1_700_000_000;
        long eightHoursLater = logout + (8 * 3600);

        // Logged out resting (inn or city): the full rate.
        (Player resting, PlayerProgression progression, RestService rest, _) = Create();
        rest.ApplyLogin(resting, new RestSnapshot(100f, logout, WasResting: true), eightHoursLater);
        Assert.Equal(110f, progression.RestBonus(resting), 3);

        // Logged out anywhere else: a quarter of it.
        (Player wild, PlayerProgression wildProgression, RestService wildRest, _) = Create();
        wildRest.ApplyLogin(wild, new RestSnapshot(100f, logout, WasResting: false), eightHoursLater);
        Assert.Equal(102.5f, wildProgression.RestBonus(wild), 3);
        Assert.Equal(102u, wild.GetUInt32(UpdateFields.PlayerRestStateExperience));
    }

    [Fact]
    public void OfflineRates_ScaleTheirOwnBranchOnly()
    {
        const long logout = 1_700_000_000;
        long later = logout + (8 * 3600);
        var options = new RestOptions { RateInGame = 0, RateOfflineInTavernOrCity = 3f, RateOfflineInWilderness = 2f };

        (Player a, PlayerProgression pa, RestService ra, _) = Create(options);
        ra.ApplyLogin(a, new RestSnapshot(0, logout, WasResting: true), later);
        Assert.Equal(30f, pa.RestBonus(a), 3);

        (Player b, PlayerProgression pb, RestService rb, _) = Create(options);
        rb.ApplyLogin(b, new RestSnapshot(0, logout, WasResting: false), later);
        Assert.Equal(5f, pb.RestBonus(b), 3); // 10 * 2 / 4

        (Player c, _, RestService rc, _) = Create(options);
        rc.SetRestType(c, RestType.InCity, 0, 0);
        rc.Update(EightHoursMs, new Environment());
        Assert.Equal(0f, rc.Capture(c, 0).RestBonus); // the in-game rate is its own option
    }

    [Fact]
    public void Login_ClampsTheStoredPool_AndIgnoresGarbageAndAClockMovedBack()
    {
        const long logout = 1_700_000_000;
        (Player player, PlayerProgression progression, RestService rest, _) = Create();

        rest.ApplyLogin(player, new RestSnapshot(1_000_000f, logout, true), logout + 3600);
        Assert.Equal(300f, progression.RestBonus(player)); // 1.5 levels

        rest.ApplyLogin(player, new RestSnapshot(float.NaN, logout, true), logout + 3600);
        Assert.Equal(0f, progression.RestBonus(player));

        rest.ApplyLogin(player, new RestSnapshot(-20f, logout, true), logout + 3600);
        Assert.Equal(0f, progression.RestBonus(player)); // the sum -20 + 1.25 is clamped to 0, as the reference does

        rest.ApplyLogin(player, new RestSnapshot(50f, logout, true), logout - 3600); // stored second in the future
        Assert.Equal(50f, progression.RestBonus(player));

        rest.ApplyLogin(player, new RestSnapshot(50f, logout, true), logout); // zero seconds
        Assert.Equal(50f, progression.RestBonus(player));

        rest.ApplyLogin(player, null, logout);
        Assert.Equal(0f, progression.RestBonus(player)); // never saved: an empty pool
    }

    [Fact]
    public void Login_AtTheMaxLevel_KeepsNoPool()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create(level: 60);
        rest.ApplyLogin(player, new RestSnapshot(500f, 0, true), 10 * 365L * 24 * 3600);
        Assert.Equal(0f, progression.RestBonus(player));
        Assert.Equal(PlayerProgression.RestStateNormal, player.GetByte(UpdateFields.PlayerBytes2, 3));
    }

    [Fact]
    public void Capture_ThenLogin_RestoresThePoolAndTheRestingFlag()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        progression.SetRestBonus(player, 123.5f);
        rest.SetRestType(player, RestType.InTavern, 5, 0);

        RestSnapshot snapshot = rest.Capture(player, 1_700_000_000);
        Assert.Equal(new RestSnapshot(123.5f, 1_700_000_000, true), snapshot);

        (Player again, PlayerProgression againProgression, RestService againRest, _) = Create();
        againRest.ApplyLogin(again, snapshot, 1_700_000_000);
        Assert.Equal(123.5f, againProgression.RestBonus(again));

        rest.SetRestType(player, RestType.None, 0, 0);
        Assert.False(rest.Capture(player, 1).WasResting);
    }

    [Fact]
    public void Forget_RemovesThePlayerFromTheWalk()
    {
        (Player player, _, RestService rest, _) = Create();
        rest.SetRestType(player, RestType.InCity, 0, 0);
        rest.Forget(player);
        Assert.Equal(0, rest.RestingCount);
        Assert.Equal(RestType.None, rest.GetRestType(player));
    }

    [Fact]
    public void LevelUp_KeepsThePoolBelowTheNewCap()
    {
        (Player player, PlayerProgression progression, RestService rest, _) = Create();
        progression.SetRestBonus(player, 300);
        progression.GiveXp(player, 450, ObjectGuid.Empty); // level 2: 900 XP to the next level, cap 675
        Assert.Equal(2, player.Level);
        Assert.Equal(300f, progression.RestBonus(player));
        rest.SetRestType(player, RestType.InCity, 0, 0);
        rest.Update(uint.MaxValue / 2, new Environment());
        Assert.Equal(675f, progression.RestBonus(player));
    }
}

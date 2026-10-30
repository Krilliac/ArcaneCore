using System.Numerics;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Groups.GroupTestWorld;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// <c>PlayerbotLfgLocality.ForPlace</c> on a real world (real player, session and planner facts): a bot standing at the Deadmines entrance may
/// advertise for a place at the entrance, the same bot thousands of yards away on the same map with no hearthstone, flight or mage service may not,
/// and a summon only flips the second case (and is never asked when the zone or the trip already allow).
/// </summary>
public sealed class PlayerbotLfgLocalityWorldTests
{
    private const byte Human = 1, Warrior = 1;
    private const uint EasternKingdoms = 0;

    // The Deadmines entrance trigger of the test content (DeadminesTestContent: -11208.6, 1679.6, 24.6 on map 0).
    private static readonly Vector3 EntrancePlace = new(-11208.6f, 1679.6f, 24.6f);

    // Some 13000 yards from the entrance on map 0 (the test map accepts any position; the planner's walk estimate decides).
    private static readonly Vector3 FarSpot = new(1800f, 240f, 24.57f);

    private static async Task<GroupTestWorld> StartAsync()
        => await GroupTestWorld.StartAsync([DungeonQuest], configure: o =>
        {
            // "No hearth/flight": the planner may only walk.
            o.Travel.Hearthstone = false;
            o.Travel.MageService = false;
            o.Taxi.Enabled = false;
        }, dungeon: true);

    [Fact]
    public async Task ABotAtTheEntrance_MayAdvertiseForAPlaceAtTheEntrance_AndNoSummonIsAsked()
    {
        await using GroupTestWorld world = await StartAsync();
        var bot = await world.AddBotAsync("Nearwarr", Human, Warrior, 20, [Taunt], EntranceArea);

        int summonAsked = 0;
        PlayerbotLfgLocalityVerdict verdict = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, EasternKingdoms, EntrancePlace, () =>
            {
                summonAsked++;
                return true;
            }));

        Assert.True(verdict.MayAdvertise, verdict.Trace);
        Assert.True(verdict.Reach is PlayerbotLfgReach.SameZone or PlayerbotLfgReach.AdjacentZone or PlayerbotLfgReach.FastTravel, $"{verdict.Reach}: {verdict.Trace}");
        Assert.NotEqual(PlayerbotLfgReach.Summon, verdict.Reach);
        Assert.Equal(0, summonAsked);
    }

    [Fact]
    public async Task TheSameBotFarAwayOnTheSameMap_WithNoHearthOrFlight_IsTooFar_AndTheSummonDecides()
    {
        await using GroupTestWorld world = await StartAsync();
        var bot = await world.AddBotAsync("Farwarr", Human, Warrior, 20, [Taunt], EntranceArea);
        Assert.Equal(600, world.Options.Chat.Voice.LfgMaxTravelSeconds);

        await world.OnWorldAsync(() =>
        {
            world.Player(bot.Id).Relocate(FarSpot.X, FarSpot.Y, FarSpot.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

        // No summon given: too far.
        PlayerbotLfgLocalityVerdict none = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, EasternKingdoms, EntrancePlace, (Func<bool>?)null));
        Assert.False(none.MayAdvertise, none.Trace);
        Assert.Equal(PlayerbotLfgReach.TooFar, none.Reach);
        Assert.False(string.IsNullOrWhiteSpace(none.Trace));

        // A summon that is not available is asked once and leaves it too far.
        int refused = 0;
        PlayerbotLfgLocalityVerdict noWarlock = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, EasternKingdoms, EntrancePlace, () =>
            {
                refused++;
                return false;
            }));
        Assert.False(noWarlock.MayAdvertise, noWarlock.Trace);
        Assert.Equal(PlayerbotLfgReach.TooFar, noWarlock.Reach);
        Assert.Equal(1, refused);

        // A summon that is available flips it.
        int asked = 0;
        PlayerbotLfgLocalityVerdict summoned = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, EasternKingdoms, EntrancePlace, () =>
            {
                asked++;
                return true;
            }));
        Assert.True(summoned.MayAdvertise, summoned.Trace);
        Assert.Equal(PlayerbotLfgReach.Summon, summoned.Reach);
        Assert.Equal(1, asked);

        // The bool overload agrees.
        PlayerbotLfgLocalityVerdict viaBool = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, EasternKingdoms, EntrancePlace, true));
        Assert.Equal(PlayerbotLfgReach.Summon, viaBool.Reach);
    }

    [Fact]
    public async Task AFarBotOnAnotherMapThanThePlace_WithNoShipOrFlight_IsTooFar_ForAPlaceInTheDungeon()
    {
        // The place is on map 36 (the Deadmines) while the bot stands on map 0: the zones cannot match and the planner has no route that fits.
        await using GroupTestWorld world = await StartAsync();
        var bot = await world.AddBotAsync("Otherwarr", Human, Warrior, 20, [Taunt], EntranceArea);
        await world.OnWorldAsync(() =>
        {
            world.Player(bot.Id).Relocate(FarSpot.X, FarSpot.Y, FarSpot.Z, 0, world.World.Host.World.NowMs);
            return true;
        });

        int asked = 0;
        PlayerbotLfgLocalityVerdict verdict = await world.OnWorldAsync(() => PlayerbotLfgLocality.ForPlace(
            world.Session(bot.Id), world.Player(bot.Id), world.Options, 36, new Vector3(-16.4f, -383.07f, 61.78f), () =>
            {
                asked++;
                return false;
            }));

        Assert.False(verdict.MayAdvertise, verdict.Trace);
        Assert.Equal(PlayerbotLfgReach.TooFar, verdict.Reach);
        Assert.Equal(1, asked);
    }
}

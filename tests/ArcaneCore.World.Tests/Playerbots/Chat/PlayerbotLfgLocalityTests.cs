using System.Numerics;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Chat;

/// <summary>
/// A bot advertised Dire Maul (Feralas) in "General - Winterspring". The advertising rule: same or adjacent zone, or a trip the real
/// planner puts under the configured limit, or a summon; otherwise the bot keeps quiet.
/// </summary>
public sealed class PlayerbotLfgLocalityTests
{
    // 1.12 AreaTable ids (tests/ThirdParty/WowWorldMessages/src/vanilla/Area.cs).
    private const uint Orgrimmar = 1637, Durotar = 14, Barrens = 17, Feralas = 357, ThousandNeedles = 400, Desolace = 405, Winterspring = 618;
    private const uint Badlands = 3, SearingGorge = 51, Alterac = 36, WesternPlaguelands = 28;
    private const uint Kalimdor = 1;
    private const int MaxTravel = 600;

    // Approximate coordinates (map 1), good enough for the planner's distance estimate: a Horde 60 in Orgrimmar and the Dire Maul entrance.
    private static readonly TravelPlace OrgrimmarSpot = new(Kalimdor, new Vector3(1630f, -4420f, 15f));
    private static readonly TravelPlace DireMaulEntrance = new(Kalimdor, new Vector3(-3520f, 1090f, 160f));

    private static TravelFacts Facts() => new() { Alliance = false, Level = 60, Money = 100_000, SpendCap = 10_000 };

    private static PlayerbotLfgLocalityVerdict FromOrgrimmar(TravelFacts facts, bool summon = false)
    {
        float? seconds = PlayerbotLfgLocality.TravelSeconds(facts, OrgrimmarSpot, DireMaulEntrance);
        return PlayerbotLfgLocality.Judge(Kalimdor, Orgrimmar, Kalimdor, Feralas, seconds, summon, MaxTravel);
    }

    [Theory]
    [InlineData(Feralas, ThousandNeedles)]
    [InlineData(Feralas, Desolace)]
    [InlineData(ThousandNeedles, Feralas)] // symmetric
    [InlineData(Desolace, Feralas)]
    [InlineData(Badlands, SearingGorge)]
    [InlineData(Alterac, WesternPlaguelands)]
    public void Adjacent_ForZonesThatBorderEachOther(uint a, uint b) => Assert.True(PlayerbotLfgLocality.Adjacent(a, b));

    [Theory]
    [InlineData(Orgrimmar, Feralas)]
    [InlineData(Feralas, Orgrimmar)]
    [InlineData(Winterspring, Feralas)]
    [InlineData(Feralas, Winterspring)]
    [InlineData(Durotar, Feralas)]
    [InlineData(Barrens, Winterspring)]
    [InlineData(Orgrimmar, Barrens)]
    public void Adjacent_IsFalseForFarApartZones(uint a, uint b) => Assert.False(PlayerbotLfgLocality.Adjacent(a, b));

    [Fact]
    public void ABotInOrgrimmarWithNoFlightNoHearth_IsTooFarFromDireMaul()
    {
        float? seconds = PlayerbotLfgLocality.TravelSeconds(Facts(), OrgrimmarSpot, DireMaulEntrance);
        Assert.NotNull(seconds);
        Assert.True(seconds > MaxTravel, $"walk estimate {seconds} s");

        PlayerbotLfgLocalityVerdict v = FromOrgrimmar(Facts());
        Assert.False(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, v.Reach);
        Assert.False(string.IsNullOrWhiteSpace(v.Trace));
    }

    [Fact]
    public void ABotInOrgrimmarWithAShortFlight_MayAdvertiseDireMaul_ByFastTravel()
    {
        TravelFacts facts = Facts() with { Flight = (_, _) => (240f, 0u) };
        float? seconds = PlayerbotLfgLocality.TravelSeconds(facts, OrgrimmarSpot, DireMaulEntrance);
        Assert.NotNull(seconds);
        Assert.True(seconds <= MaxTravel, $"flight estimate {seconds} s");

        PlayerbotLfgLocalityVerdict v = FromOrgrimmar(facts);
        Assert.True(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.FastTravel, v.Reach);
    }

    [Fact]
    public void ABotInOrgrimmarWithAReadyHearthstoneBoundNearTheEntrance_MayAdvertiseDireMaul()
    {
        TravelFacts facts = Facts() with { HearthReady = true, Bind = new TravelPlace(Kalimdor, new Vector3(-3500f, 1100f, 160f)) };
        float? seconds = PlayerbotLfgLocality.TravelSeconds(facts, OrgrimmarSpot, DireMaulEntrance);
        Assert.NotNull(seconds);
        Assert.True(seconds <= MaxTravel, $"hearth estimate {seconds} s");

        PlayerbotLfgLocalityVerdict v = FromOrgrimmar(facts);
        Assert.True(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.FastTravel, v.Reach);
    }

    [Fact]
    public void AHearthstoneBoundFarFromTheEntrance_DoesNotMakeItReachable()
    {
        TravelFacts facts = Facts() with { HearthReady = true, Bind = new TravelPlace(Kalimdor, OrgrimmarSpot.Position + new Vector3(30f, 0f, 0f)) };
        PlayerbotLfgLocalityVerdict v = FromOrgrimmar(facts);
        Assert.False(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, v.Reach);
    }

    [Fact]
    public void ABotInThousandNeedles_MayAdvertiseDireMaul_EvenWithoutATravelEstimate()
    {
        PlayerbotLfgLocalityVerdict v = PlayerbotLfgLocality.Judge(Kalimdor, ThousandNeedles, Kalimdor, Feralas, null, false, MaxTravel);
        Assert.True(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.AdjacentZone, v.Reach);
    }

    [Fact]
    public void ABotInTheSameZone_MayAdvertise_ButZeroIsNoZone()
    {
        PlayerbotLfgLocalityVerdict same = PlayerbotLfgLocality.Judge(Kalimdor, Feralas, Kalimdor, Feralas, null, false, MaxTravel);
        Assert.True(same.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.SameZone, same.Reach);

        PlayerbotLfgLocalityVerdict unknown = PlayerbotLfgLocality.Judge(Kalimdor, 0, Kalimdor, 0, null, false, MaxTravel);
        Assert.False(unknown.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, unknown.Reach);
    }

    [Fact]
    public void ASameZoneIdOnAnotherMap_IsNotTheSameZone()
    {
        PlayerbotLfgLocalityVerdict v = PlayerbotLfgLocality.Judge(0, Feralas, Kalimdor, Feralas, null, false, MaxTravel);
        Assert.False(v.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, v.Reach);
    }

    [Fact]
    public void ABotInWinterspring_WithNoWayToDireMaul_IsTooFar_AndASummonMakesItReachable()
    {
        PlayerbotLfgLocalityVerdict far = PlayerbotLfgLocality.Judge(Kalimdor, Winterspring, Kalimdor, Feralas, null, false, MaxTravel);
        Assert.False(far.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, far.Reach);
        Assert.Contains("no route", far.Trace, StringComparison.Ordinal);

        PlayerbotLfgLocalityVerdict summoned = PlayerbotLfgLocality.Judge(Kalimdor, Winterspring, Kalimdor, Feralas, null, true, MaxTravel);
        Assert.True(summoned.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.Summon, summoned.Reach);
    }

    [Fact]
    public void TheTravelLimit_IsInclusive_AndAFasterTripBeatsTheSummon()
    {
        PlayerbotLfgLocalityVerdict atLimit = PlayerbotLfgLocality.Judge(Kalimdor, Winterspring, Kalimdor, Feralas, 600f, true, MaxTravel);
        Assert.Equal(PlayerbotLfgReach.FastTravel, atLimit.Reach);

        PlayerbotLfgLocalityVerdict over = PlayerbotLfgLocality.Judge(Kalimdor, Winterspring, Kalimdor, Feralas, 601f, false, MaxTravel);
        Assert.False(over.MayAdvertise);
        Assert.Equal(PlayerbotLfgReach.TooFar, over.Reach);

        Assert.Equal(PlayerbotLfgReach.Summon, PlayerbotLfgLocality.Judge(Kalimdor, Winterspring, Kalimdor, Feralas, 601f, true, MaxTravel).Reach);
    }
}

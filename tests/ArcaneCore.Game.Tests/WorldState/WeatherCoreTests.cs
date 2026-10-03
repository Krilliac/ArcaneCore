using ArcaneCore.Game.WorldState.Weather;
using Xunit;

namespace ArcaneCore.Game.Tests.WorldState;

/// <summary>
/// The weather state machine against vmangos Weather.cpp:87-210 (mangos-classic's is identical),
/// branch by branch with a scripted random source.
/// </summary>
public sealed class WeatherCoreTests
{
    private sealed class Script(uint[] ints, float[] floats) : IWeatherRandom
    {
        private int _i;
        private int _f;

        public uint Next(uint min, uint max)
        {
            uint v = ints[_i++];
            Assert.InRange(v, min, max);
            return v;
        }

        public float NextFloat() => floats[_f++];

        public void AssertConsumed()
        {
            Assert.Equal(ints.Length, _i);
            Assert.Equal(floats.Length, _f);
        }
    }

    private static ZoneWeatherChances Chances(uint rain, uint snow, uint storm)
        => ZoneWeatherChances.FromColumns(1, [rain, snow, storm, rain, snow, storm, rain, snow, storm, rain, snow, storm]);

    private static WeatherState State(WeatherType type, float grade, ZoneWeatherChances? chances)
    {
        var state = new WeatherState(1, chances);
        state.SetWeather(type, grade);
        return state;
    }

    [Theory]
    [InlineData(2024, 1, 1, WeatherSeason.Winter)]
    [InlineData(2023, 1, 1, WeatherSeason.Winter)]
    [InlineData(2023, 3, 20, WeatherSeason.Spring)]
    [InlineData(2023, 6, 20, WeatherSeason.Summer)]
    [InlineData(2023, 9, 22, WeatherSeason.Fall)]
    [InlineData(2023, 12, 21, WeatherSeason.Winter)]
    public void Season_FollowsTheVmangosDayOfYearFormula(int year, int month, int day, WeatherSeason expected)
        => Assert.Equal(expected, WeatherSeasons.Of(new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void NoChange_In30Percent_AndPermanentWeatherNeverChanges()
    {
        WeatherState state = State(WeatherType.Rain, 0.5f, Chances(20, 0, 0));
        var rng = new Script([29], []);
        Assert.False(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal((WeatherType.Rain, 0.5f), (state.Type, state.Grade));
        rng.AssertConsumed();

        state.IsPermanent = true;
        Assert.False(state.ReGenerate(WeatherSeason.Spring, new Script([], [])));
    }

    [Fact]
    public void GetBetter_SubtractsAThird()
    {
        WeatherState state = State(WeatherType.Rain, 0.5f, Chances(20, 0, 0));
        var rng = new Script([45], []);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal(WeatherType.Rain, state.Type);
        Assert.Equal(0.5f - 0.33333334f, state.Grade);
        rng.AssertConsumed();
    }

    [Fact]
    public void GetWorse_AddsAThird_AndDoesNotNormalizeThePastOne()
    {
        WeatherState state = State(WeatherType.Rain, 0.5f, Chances(20, 0, 0));
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([75], [])));
        Assert.Equal(0.5f + 0.33333334f, state.Grade);

        // vmangos returns before NormalizeGrade here: 0.9 + 1/3 stays above 1 until a packet is built.
        state = State(WeatherType.Rain, 0.9f, Chances(20, 0, 0));
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([75], [])));
        Assert.True(state.Grade > 1f);
        byte[] packet = state.BuildPacket();
        Assert.Equal(0.9999f, state.Grade);
        Assert.Equal(13, packet.Length);
    }

    [Fact]
    public void RadicalChange_LightGoesNuts()
    {
        WeatherState state = State(WeatherType.Rain, 0.2f, Chances(20, 0, 0));
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([95], [])));
        Assert.Equal((WeatherType.Rain, 0.9999f), (state.Type, state.Grade));
    }

    [Fact]
    public void RadicalChange_Heavy_HalfDropsByTwoThirds_HalfClearsAndRerolls()
    {
        // 0.6666667f, not 0.6667f: the threshold and the subtraction use the C++ constant.
        WeatherState state = State(WeatherType.Rain, 0.8f, Chances(20, 0, 0));
        var rng = new Script([95, 10], []);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal((WeatherType.Rain, 0.8f - 0.6666667f), (state.Type, state.Grade));
        rng.AssertConsumed();

        // severity roll 60: clear up, then a fresh roll; 95 > 20 -> fine
        state = State(WeatherType.Rain, 0.8f, Chances(20, 0, 0));
        rng = new Script([95, 60, 95], []);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal((WeatherType.Fine, 0f), (state.Type, state.Grade));
        rng.AssertConsumed();
    }

    [Fact]
    public void RadicalChange_Medium_ClearsUpAndRerolls()
    {
        WeatherState state = State(WeatherType.Rain, 0.5f, Chances(0, 0, 20));
        // u=95: medium weather clears up (no severity roll, 0.5 is not above 2/3), the fresh roll 10 hits storm (20),
        // and u >= 90 adds the severe-weather roll 10 (< 50).
        var rng = new Script([95, 10, 10], [0.5f]);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal(WeatherType.Storm, state.Type);
        Assert.Equal((0.5f * 0.3333f) + 0.3334f, state.Grade);
        rng.AssertConsumed();
    }

    [Fact]
    public void GetFair_FallsThroughIntoAFreshRoll()
    {
        // (RAIN, 0.2) with u=45: grade < 1/3 -> FINE, then the better/worse/radical branches are skipped
        // (type is fine) and a new weather is rolled from the chances table.
        WeatherState state = State(WeatherType.Rain, 0.2f, Chances(0, 0, 20));
        var rng = new Script([45, 15], [0.5f]);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, rng));
        Assert.Equal(WeatherType.Storm, state.Type);
        Assert.Equal(0.5f * 0.3333f, state.Grade);
        rng.AssertConsumed();
    }

    [Fact]
    public void FreshRoll_UsesInclusiveCumulativeChances()
    {
        ZoneWeatherChances rainOnly = Chances(20, 0, 0);
        WeatherState state = State(WeatherType.Fine, 0, rainOnly);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([40, 20], [0f])));
        Assert.Equal(WeatherType.Rain, state.Type);

        state = State(WeatherType.Fine, 0, rainOnly);
        Assert.False(state.ReGenerate(WeatherSeason.Spring, new Script([40, 21], [])));
        Assert.Equal(WeatherType.Fine, state.Type);

        ZoneWeatherChances mixed = Chances(10, 10, 10);
        foreach ((uint roll, WeatherType expected) in new[] { (10u, WeatherType.Rain), (11u, WeatherType.Snow), (20u, WeatherType.Snow), (21u, WeatherType.Storm), (30u, WeatherType.Storm), (31u, WeatherType.Fine) })
        {
            state = State(WeatherType.Fine, 0, mixed);
            state.ReGenerate(WeatherSeason.Spring, new Script([40, roll], expected == WeatherType.Fine ? [] : [0.5f]));
            Assert.Equal(expected, state.Type);
        }
    }

    [Fact]
    public void FreshRoll_Severe10Percent_RaisesTheGrade()
    {
        WeatherState state = State(WeatherType.Fine, 0, Chances(100, 0, 0));
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([95, 1, 10], [0.5f])));
        Assert.Equal((0.5f * 0.3333f) + 0.3334f, state.Grade);

        state = State(WeatherType.Fine, 0, Chances(100, 0, 0));
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([95, 1, 60], [0.5f])));
        Assert.Equal((0.5f * 0.3333f) + 0.6667f, state.Grade);
    }

    [Fact]
    public void ZoneWithoutChances_StaysFine_AndReportsAChangeOnlyFromNonFine()
    {
        WeatherState state = State(WeatherType.Rain, 0.5f, null);
        Assert.True(state.ReGenerate(WeatherSeason.Spring, new Script([], [])));
        Assert.Equal((WeatherType.Fine, 0f), (state.Type, state.Grade));
        Assert.False(state.ReGenerate(WeatherSeason.Spring, new Script([], [])));
    }

    [Fact]
    public void NormalizeGrade_ClampsLikeVmangos()
    {
        var state = new WeatherState(1, null);
        state.SetWeather(WeatherType.Rain, 1.0f);
        Assert.Equal(0.9999f, state.Grade);
        state.SetWeather(WeatherType.Rain, -0.2f);
        Assert.Equal(0.0001f, state.Grade);
        state.SetWeather(WeatherType.Rain, 0f);
        Assert.Equal(0f, state.Grade);
    }

    [Theory]
    [InlineData(WeatherType.Rain, 0.2f, 0u)]
    [InlineData(WeatherType.Rain, 0.5f, 8533u)]
    [InlineData(WeatherType.Rain, 0.7f, 8534u)]
    [InlineData(WeatherType.Rain, 0.95f, 8535u)]
    [InlineData(WeatherType.Snow, 0.5f, 8536u)]
    [InlineData(WeatherType.Snow, 0.7f, 8537u)]
    [InlineData(WeatherType.Snow, 0.95f, 8538u)]
    [InlineData(WeatherType.Storm, 0.5f, 8556u)]
    [InlineData(WeatherType.Storm, 0.7f, 8557u)]
    [InlineData(WeatherType.Storm, 0.95f, 8558u)]
    [InlineData(WeatherType.Fine, 0.95f, 0u)]
    public void Sound_FollowsTheVmangosTable(WeatherType type, float grade, uint sound)
        => Assert.Equal(sound, WeatherSounds.For(type, grade));

    [Fact]
    public void Packet_Is13Bytes_InTheGtkerLayout()
    {
        Assert.Equal(
            [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F, 0x55, 0x21, 0x00, 0x00, 0x00],
            WeatherPackets.Build(WeatherType.Rain, 0.5f, 8533));
        Assert.Equal(1, WeatherPackets.Build(WeatherType.Fine, 0f, 0, instant: true)[12]);
    }

    [Fact]
    public void ChanceLoader_ReplacesValuesAbove100With25_AndReportsThem()
    {
        var errors = new List<string>();
        ZoneWeatherChances chances = ZoneWeatherChances.FromColumns(12, [120, 0, 0, 20, 101, 100, 0, 0, 0, 0, 0, 255], errors.Add);
        Assert.Equal(new SeasonChances(25, 0, 0), chances[WeatherSeason.Spring]);
        Assert.Equal(new SeasonChances(20, 25, 100), chances[WeatherSeason.Summer]);
        Assert.Equal(new SeasonChances(0, 0, 25), chances[WeatherSeason.Winter]);
        Assert.Equal(3, errors.Count);
        Assert.Throws<ArgumentException>(() => ZoneWeatherChances.FromColumns(1, [1, 2, 3]));
    }

    private sealed class Seeded(int seed) : IWeatherRandom
    {
        private readonly Random _random = new(seed);

        public uint Next(uint min, uint max) => (uint)_random.NextInt64(min, (long)max + 1);

        public float NextFloat() => (float)_random.NextDouble();
    }

    [Fact]
    public void LongRun_MatchesTheDocumentedDistribution()
    {
        // Self-consistency with an independent python port of the same C++ (not a retail oracle):
        // 400k regens with a 20% rain zone: the python port spent 47.6% of the time at
        // grade >= 0.27 (the vmangos GetWeatherState "not fine" threshold). The design note also said 69.9% of
        // regens change state; that is the share that passes the 30% no-change gate (an upper bound: a fine
        // zone that re-rolls fine does not change), so the measured 46.5% is pinned instead.
        var state = new WeatherState(1, Chances(20, 0, 0));
        var rng = new Seeded(20261003);
        const int Runs = 400_000;
        int changed = 0;
        int wet = 0;
        for (int i = 0; i < Runs; i++)
        {
            if (state.ReGenerate(WeatherSeason.Spring, rng))
            {
                changed++;
            }

            if (state.Type != WeatherType.Fine && state.Grade >= 0.27f)
            {
                wet++;
            }
        }

        Assert.InRange(changed / (double)Runs, 0.455, 0.475);
        Assert.InRange(wet / (double)Runs, 0.456, 0.496);
    }
}

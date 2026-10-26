using System.Globalization;
using System.Reflection;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// <c>World:Playerbots:Groups:WorldBosses</c> (<see cref="PlayerbotWorldBossOptions"/>, docs/areas/playbots-groups.md, World-boss raids):
/// the 24 keys, their defaults (docs: p7b-world-boss-design.md section 5, owner decisions W1 to W3 as proposed), their ranges and the
/// CSV forms of the three list keys. The keys' registration in <c>WorldConfigKeys</c> is checked by <c>ConfigReloadTests</c>, the generated
/// docs by the Docs tests.
/// </summary>
public sealed class PlayerbotWorldBossOptionsTests
{
    private static readonly string[] Keys =
    [
        "Enabled", "WatchSeconds", "PlayerGraceMinutes", "PlayersNearYards", "PlayersNearCount", "MusterRadiusYards", "LevelGap", "MinRaidSize",
        "DefaultRaidSize", "SizeOverrides", "MusterWindowSeconds", "MaxConcurrentRaids", "MaxAttemptsPerDay", "RetrySeconds", "RallyYards",
        "PresentFraction", "PullWaitSeconds", "HoldSeconds", "MaxWipes", "MaxRaidMinutes", "CatchUpTeleport", "DeriveFromContent",
        "IncludedEntries", "ExcludedEntries",
    ];

    private static void Set(PlayerbotWorldBossOptions options, string key, double value)
    {
        PropertyInfo property = typeof(PlayerbotWorldBossOptions).GetProperty(key)!;
        property.SetValue(options, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheSection_HasExactlyTheDocumentedKeys_AndNoKillTalkSwitch()
    {
        string[] actual = [.. typeof(PlayerbotWorldBossOptions).GetProperties().Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name).Order()];
        Assert.Equal(24, actual.Length);
        Assert.Equal(Keys.Order(), actual);
        Assert.DoesNotContain("KillTalk", actual); // W4: the kill-talk fix has no switch
    }

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes_AndTheNewBehaviourIsOn()
    {
        var o = new PlayerbotWorldBossOptions();
        Assert.True(o.Enabled);
        Assert.Equal(30, o.WatchSeconds);
        Assert.Equal(15, o.PlayerGraceMinutes);
        Assert.Equal(100f, o.PlayersNearYards);
        Assert.Equal(3, o.PlayersNearCount);
        Assert.Equal(3000f, o.MusterRadiusYards);   // lowered from 6000: the raid walks
        Assert.Equal(2, o.LevelGap);
        Assert.Equal(30, o.MinRaidSize);
        Assert.Equal(40, o.DefaultRaidSize);
        Assert.Equal("", o.SizeOverrides);
        Assert.Equal(180, o.MusterWindowSeconds);
        Assert.Equal(1, o.MaxConcurrentRaids);
        Assert.Equal(3, o.MaxAttemptsPerDay);
        Assert.Equal(1800, o.RetrySeconds);
        Assert.Equal(50f, o.RallyYards);
        Assert.Equal(0.8f, o.PresentFraction);
        Assert.Equal(120, o.PullWaitSeconds);
        Assert.Equal(180, o.HoldSeconds);
        Assert.Equal(2, o.MaxWipes);
        Assert.Equal(90, o.MaxRaidMinutes);
        Assert.False(o.CatchUpTeleport);            // W3: walk by default
        Assert.True(o.DeriveFromContent);
        Assert.Equal("", o.IncludedEntries);
        Assert.Equal("", o.ExcludedEntries);
        o.Validate();
    }

    [Fact]
    public void TheGroupOptions_CarryTheSection_AndValidateIt()
    {
        var groups = new PlayerbotGroupOptions();
        Assert.NotNull(groups.WorldBosses);
        groups.Validate();

        groups.WorldBosses.WatchSeconds = 1;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(groups.Validate);
        Assert.Contains("WatchSeconds", error.Message, StringComparison.Ordinal);

        groups.WorldBosses = null!;
        Assert.Contains("WorldBosses", Assert.Throws<InvalidOperationException>(groups.Validate).Message, StringComparison.Ordinal);
    }

    /// <summary>(key, lowest valid, highest valid): the ranges of the design's options table.</summary>
    public static TheoryData<string, double, double> Ranges => new()
    {
        { "WatchSeconds", 5, 600 }, { "PlayerGraceMinutes", 0, 240 }, { "PlayersNearYards", 0, 400 }, { "PlayersNearCount", 1, 40 },
        { "MusterRadiusYards", 300, 20000 }, { "LevelGap", 0, 20 }, { "MinRaidSize", 5, 40 }, { "DefaultRaidSize", 5, 40 },
        { "MusterWindowSeconds", 30, 1800 }, { "MaxConcurrentRaids", 0, 8 }, { "MaxAttemptsPerDay", 0, 24 }, { "RetrySeconds", 60, 86400 },
        { "RallyYards", 20, 200 }, { "PresentFraction", 0.5, 1.0 }, { "PullWaitSeconds", 10, 900 }, { "HoldSeconds", 10, 1800 },
        { "MaxWipes", 1, 10 }, { "MaxRaidMinutes", 10, 480 },
    };

    [Theory]
    [MemberData(nameof(Ranges))]
    public void EachNumericKey_AcceptsItsBounds_AndRefusesJustOutsideThem(string key, double low, double high)
    {
        double below = key == "PresentFraction" ? 0.49 : low - 1, above = key == "PresentFraction" ? 1.01 : high + 1;
        foreach (double ok in new[] { low, high })
        {
            PlayerbotWorldBossOptions good = WithMinimumsFor(key);
            Set(good, key, ok);
            good.Validate();
        }

        foreach (double bad in new[] { below, above })
        {
            PlayerbotWorldBossOptions refused = WithMinimumsFor(key);
            Set(refused, key, bad);
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(refused.Validate);
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>MinRaidSize may not exceed DefaultRaidSize, so the DefaultRaidSize bound test starts from the smallest minimum.</summary>
    private static PlayerbotWorldBossOptions WithMinimumsFor(string key) => key == "DefaultRaidSize" ? new PlayerbotWorldBossOptions { MinRaidSize = 5 } : new PlayerbotWorldBossOptions();

    [Fact]
    public void MaxWipes_IsNowOneToTen_ZeroHadNoMeaning()
    {
        var o = new PlayerbotWorldBossOptions { MaxWipes = 0 };
        Assert.Contains("MaxWipes", Assert.Throws<InvalidOperationException>(o.Validate).Message, StringComparison.Ordinal);
        o.MaxWipes = 10;
        o.Validate();
        o.MaxWipes = 11;
        Assert.Throws<InvalidOperationException>(o.Validate);
    }

    [Fact]
    public void MinRaidSize_AboveDefaultRaidSize_IsRefused()
    {
        var o = new PlayerbotWorldBossOptions { MinRaidSize = 31, DefaultRaidSize = 30 };
        Assert.Throws<InvalidOperationException>(o.Validate);
        o.MinRaidSize = 30;
        o.Validate();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("6109:25", true)]
    [InlineData("6109:5,12397:40", true)]
    [InlineData("6109:4", false)]
    [InlineData("6109:41", false)]
    [InlineData("6109", false)]
    [InlineData("six:20", false)]
    [InlineData("6109:2.5", false)]
    public void SizeOverrides_AreEntryColonSize_WithinFiveToForty(string value, bool valid)
        => AssertValid(new PlayerbotWorldBossOptions { SizeOverrides = value }, "SizeOverrides", valid);

    [Theory]
    [InlineData("", true)]
    [InlineData("7846", true)]
    [InlineData("7846@0", true)]
    [InlineData("990810@1", true)]
    [InlineData("7846,990810@0,990811@1", true)]
    [InlineData("990810@2", false)]   // a continent: map 0 or 1
    [InlineData("990810@36", false)]
    [InlineData("990810@", false)]
    [InlineData("@0", false)]
    [InlineData("boss", false)]
    public void IncludedEntries_AreAnEntryOrEntryAtMap_OnAContinent(string value, bool valid)
        => AssertValid(new PlayerbotWorldBossOptions { IncludedEntries = value }, "IncludedEntries", valid);

    [Theory]
    [InlineData("", true)]
    [InlineData("6109", true)]
    [InlineData("6109,7846", true)]
    [InlineData("6109@0", false)]
    [InlineData("six", false)]
    public void ExcludedEntries_AreEntryNumbers(string value, bool valid)
        => AssertValid(new PlayerbotWorldBossOptions { ExcludedEntries = value }, "ExcludedEntries", valid);

    private static void AssertValid(PlayerbotWorldBossOptions options, string key, bool valid)
    {
        if (valid)
        {
            options.Validate();
            return;
        }

        Assert.Contains(key, Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }
}

using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Social;

/// <summary>
/// <see cref="GuildOptions"/> defaults equal the vmangos/retail values (mangosd.conf.dist.in:1296,
/// 1299, 1341; World.cpp:621,625,666) and bind from <c>World:Guild</c>.
/// </summary>
public sealed class GuildOptionsTests
{
    [Fact]
    public void Defaults_AreTheRetailReferenceValues()
    {
        var options = new GuildOptions();

        Assert.Equal(9, options.MinPetitionSigns);          // MinPetitionSigns = 9 (conf:1341)
        Assert.Equal(2, options.MinCharterNameLength);       // MinCharterName = 2 (conf:1299)
        Assert.Equal(0, options.StrictCharterNames);         // StrictCharterNames = 0 (conf:1296)
        Assert.False(options.AllowClientGuildCreate);
        Assert.False(options.DeleteRankMovesMembers);        // vmangos Guild::DelRank leaves members on the dead id
        Assert.True(options.KickOnOversizedText);            // vmangos GuildHandler.cpp ProcessAnticheatAction kick
    }

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(9, 9)]
    [InlineData(40, 9)]
    public void MinPetitionSigns_IsClampedLikeVmangos(int configured, int effective)
    {
        // World.cpp:666 setConfigMinMax("MinPetitionSigns", 9, 0, 9).
        Assert.Equal(effective, new GuildOptions { MinPetitionSigns = configured }.EffectiveMinPetitionSigns);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(2, 2)]
    [InlineData(10, 10)]
    [InlineData(99, 24)]
    public void MinCharterNameLength_IsClampedLikeVmangos(int configured, int effective)
    {
        // World.cpp:625 setConfigMinMax("MinCharterName", 2, 2, MAX_CHARTER_NAME).
        Assert.Equal(effective, new GuildOptions { MinCharterNameLength = configured }.EffectiveMinCharterNameLength);
    }

    [Fact]
    public void GuildOptions_AreNotPartOfTheLiveReloadedSocialOptions()
    {
        // World:Guild is restart-only (docs/areas/social.md); it must not become a SocialOptions
        // member, or the WorldConfigKeys guard would demand a live/restart-only classification.
        Assert.DoesNotContain(typeof(SocialOptions).GetProperties(), p => p.PropertyType == typeof(GuildOptions));
    }
}

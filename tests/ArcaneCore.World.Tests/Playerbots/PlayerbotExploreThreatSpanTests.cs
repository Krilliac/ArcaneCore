using System.Numerics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// <see cref="PlayerbotExploreAtlas.HasStrongerNear"/> has to cover the same square as
/// <see cref="PlayerbotExploreAtlas.HasCellNear"/>. A radius of <c>rings * 160</c> yards from the threat cell under the bot
/// stops one 80-yard cell short of that square's far side, so a pack in the missed strip did not count and an under-levelled
/// bot was left on Explore.
/// </summary>
public sealed class PlayerbotExploreThreatSpanTests
{
    private const uint Level = 8;
    private const int Margin = 2;

    [Fact]
    public void StrongerCreature_InTheFarHalfOfTheBotsOwnCell_IsNearAtRingZero()
    {
        // Explore cell 0 is [0, 160). The bot stands in its low half; the pack stands in the high half, still the same cell.
        const float bot = 10f, pack = 150f;
        Assert.Equal(0, PlayerbotExploreAtlas.CellOf(bot));
        Assert.Equal(0, PlayerbotExploreAtlas.CellOf(pack));
        PlayerbotExploreAtlas atlas = Atlas(pack, 10f);

        Assert.True(atlas.HasStrongerNear(0, Level, new Vector3(bot, 10f, 20f), rings: 0, Margin));
        Assert.False(atlas.HasCellNear(0, Level, new Vector3(bot, 10f, 20f), rings: 0),
            "the pack is too high to be a grind cell; the stranded check depends on the threat index alone");
    }

    [Fact]
    public void StrongerCreature_InTheFarHalfOfTheOuterRing_IsNear_AndOneCellPastItIsNot()
    {
        const float bot = 10f;
        const int rings = 1;
        float inside = 2 * PlayerbotExploreAtlas.CellYards - 10f; // 310: far half of explore cell 1, inside the square
        float outside = 2 * PlayerbotExploreAtlas.CellYards + 10f; // 330: explore cell 2, one cell past the square
        Assert.Equal(1, PlayerbotExploreAtlas.CellOf(inside));
        Assert.Equal(2, PlayerbotExploreAtlas.CellOf(outside));
        var at = new Vector3(bot, 10f, 20f);

        Assert.True(Atlas(inside, 10f).HasStrongerNear(0, Level, at, rings, Margin));
        Assert.False(Atlas(outside, 10f).HasStrongerNear(0, Level, at, rings, Margin));
    }

    [Fact]
    public void StrongerCreature_InTheFarHalfOfANegativeCell_IsNear()
    {
        // Explore cell -1 is [-160, 0). The bot stands in the half toward zero; the pack stands in the half away from it.
        const float bot = -10f, pack = -150f, past = -200f;
        Assert.Equal(-1, PlayerbotExploreAtlas.CellOf(bot));
        Assert.Equal(-1, PlayerbotExploreAtlas.CellOf(pack));
        Assert.Equal(-2, PlayerbotExploreAtlas.CellOf(past));
        var at = new Vector3(bot, bot, 20f);

        Assert.True(Atlas(pack, bot).HasStrongerNear(0, Level, at, rings: 0, Margin));
        Assert.False(Atlas(past, bot).HasStrongerNear(0, Level, at, rings: 0, Margin));
    }

    private static PlayerbotExploreAtlas Atlas(float x, float y)
    {
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 1, Name = "strong", MinLevel = 20, MaxLevel = 20, Faction = 14 }],
            [new CreatureSpawn { Guid = 1, Entry = 1, MapId = 0, X = x, Y = y, Z = 20 }], [], [], []);
        return new PlayerbotExploreAtlas(content);
    }
}

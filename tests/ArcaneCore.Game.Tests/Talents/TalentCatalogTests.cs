using ArcaneCore.Kernel.Talents;
using Xunit;

namespace ArcaneCore.Game.Tests.Talents;

public sealed class TalentCatalogTests
{
    private static TalentRecord T(uint id, uint tab, uint row, uint[] ranks, uint dependsOn = 0, uint dependsOnRank = 0, uint dependsOnSpell = 0, uint col = 0)
        => new(id, tab, row, col, ranks, dependsOn, dependsOnRank, dependsOnSpell);

    private static readonly TalentTabRecord[] Tabs = [new(1, 1u << 0, 0), new(2, 1u << 2, 1)];

    [Fact]
    public void ByRankSpell_ReturnsTalentAndRankIndex()
    {
        var catalog = new TalentCatalog(Tabs, [T(10, 1, 0, [100, 101, 102, 0, 0]), T(11, 2, 0, [200, 0, 0, 0, 0])]);
        Assert.True(catalog.TryGetRankPosition(101, out TalentRankPosition position));
        Assert.Equal(new TalentRankPosition(10, 1), position);
        Assert.True(catalog.TryGetRankPosition(200, out position));
        Assert.Equal(new TalentRankPosition(11, 0), position);
        Assert.False(catalog.TryGetRankPosition(999, out _));
        Assert.False(catalog.TryGetRankPosition(0, out _));
    }

    [Fact]
    public void Lookups_ByIdTabAndClassMask()
    {
        var catalog = new TalentCatalog(Tabs, [T(10, 1, 0, [100, 0, 0, 0, 0]), T(12, 1, 1, [120, 0, 0, 0, 0]), T(11, 2, 0, [200, 0, 0, 0, 0])]);
        Assert.Equal(3, catalog.TalentCount);
        Assert.Null(catalog.ById(99));
        Assert.Equal<uint>([10, 12], catalog.TalentsOfTab(1).Select(t => t.Id));
        Assert.Empty(catalog.TalentsOfTab(77));
        Assert.Equal<uint>([2], catalog.TabsForClassMask(1u << 2).Select(t => t.Id));
        Assert.Equal<uint>([1, 2], catalog.TabsForClassMask((1u << 0) | (1u << 2)).Select(t => t.Id));
        Assert.Empty(catalog.TabsForClassMask(1u << 5));
    }

    [Fact]
    public void DuplicateRankSpellAcrossTalents_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 0, [100, 0, 0, 0, 0]), T(11, 2, 0, [100, 0, 0, 0, 0])]));

    [Fact]
    public void DuplicateRankSpellInsideOneTalent_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 0, [100, 100, 0, 0, 0])]));

    [Fact]
    public void DuplicateTalentId_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 0, [100, 0, 0, 0, 0]), T(10, 1, 0, [101, 0, 0, 0, 0])]));

    [Fact]
    public void DuplicateTabId_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog([new TalentTabRecord(1, 1, 0), new TalentTabRecord(1, 2, 1)], []));

    [Fact]
    public void RankGap_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 0, [100, 0, 102, 0, 0])]));

    [Fact]
    public void WrongRankArrayLength_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 0, [100, 101, 102, 103])]));

    [Fact]
    public void UnknownTab_Throws()
        => Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 9, 0, [100, 0, 0, 0, 0])]));

    [Fact]
    public void RowAboveTen_Throws()
    {
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 11, [100, 0, 0, 0, 0])]));
        _ = new TalentCatalog(Tabs, [T(10, 1, 10, [100, 0, 0, 0, 0])]);
    }

    [Fact]
    public void DanglingSelfAndCyclicPrerequisites_Throw()
    {
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 1, [100, 0, 0, 0, 0], dependsOn: 77)]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [T(10, 1, 1, [100, 0, 0, 0, 0], dependsOn: 10)]));
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs,
        [
            T(10, 1, 1, [100, 0, 0, 0, 0], dependsOn: 11),
            T(11, 1, 1, [110, 0, 0, 0, 0], dependsOn: 10),
        ]));
    }

    [Fact]
    public void PrerequisiteRankMustFitTheTargetRankCount()
    {
        TalentRecord prerequisite = T(10, 1, 0, [100, 101, 0, 0, 0]);
        Assert.Throws<InvalidDataException>(() => new TalentCatalog(Tabs, [prerequisite, T(11, 1, 1, [110, 0, 0, 0, 0], dependsOn: 10, dependsOnRank: 2)]));
        _ = new TalentCatalog(Tabs, [prerequisite, T(11, 1, 1, [110, 0, 0, 0, 0], dependsOn: 10, dependsOnRank: 1)]);
    }
}

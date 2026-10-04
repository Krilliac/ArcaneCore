using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Threat;
using Xunit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>The live spell_threat table: swapped rows, the rank fill when the chains arrive late, the entry a lookup returns.</summary>
public sealed class SpellThreatTableTests
{
    private static SpellRankChains Chain(params (uint Spell, uint Forward)[] links)
        => new(links.Select((l, i) => new SkillLineAbilityRecord((uint)i + 1, 26, l.Spell, 0, 0, 0, l.Forward, 0, 0, 0)));

    [Fact]
    public void FindReturnsTheEntry_ASpellWithoutARowHasNone_AndReplaceSwapsTheRows()
    {
        var table = new SpellThreatTable();
        Assert.Null(table.Find(72));

        table.Replace(new SpellThreatContent([new SpellThreatRecord(72, 180, 1.5f, 2)]));
        SpellThreatEntry entry = table.Find(72)!;
        Assert.Equal((72u, 180, 1.5f, (byte)2), (entry.SpellId, entry.Threat, entry.Multiplier, entry.InverseEffectMask));
        Assert.Null(table.Find(73));

        table.Replace(SpellThreatContent.Empty);
        Assert.Null(table.Find(72));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void TheRanksAreFilledWhenTheChainsArriveAfterTheRows()
    {
        SpellRankChains? chains = null;
        var table = new SpellThreatTable(() => chains);
        table.Replace(new SpellThreatContent([new SpellThreatRecord(100, 80, 1f, 0)]));

        chains = Chain((100, 101), (101, 0)); // the skill content finished loading after the table

        Assert.Equal(80, table.Find(101)!.Threat);
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void SpellsThatDoNotExistAreDroppedAndReported()
    {
        var reports = new List<string>();
        var table = new SpellThreatTable(spellExists: id => id != 9, report: reports.Add);
        table.Replace(new SpellThreatContent([new SpellThreatRecord(9, 5, 1f, 0), new SpellThreatRecord(10, 5, 1f, 0)]));

        Assert.Null(table.Find(9));
        Assert.NotNull(table.Find(10));
        Assert.Contains("Spell 9", Assert.Single(reports), StringComparison.Ordinal);
    }

    [Fact]
    public void CanCauseThreatOnMask_IsTrueWhenSomeSelectedEffectIsNotInverted()
    {
        var entry = new SpellThreatEntry(1, 10, 1f, InverseEffectMask: 0b001);

        Assert.False(entry.CanCauseThreatOnMask(0b001));
        Assert.True(entry.CanCauseThreatOnMask(0b011));
        Assert.True(entry.CanCauseThreatOnMask(0b100));
        Assert.False(entry.CanCauseThreatOnMask(0));
    }
}

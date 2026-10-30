using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using Xunit;

namespace ArcaneCore.Game.Tests.Talents;

/// <summary>
/// Trainer-sold higher ranks of single-rank talent abilities (Pyroblast 11366 -> 12505 -> 12522) have forward_spellid 0 in
/// SkillLineAbility.dbc; the reference gets the link from spell_chain prev_spell, without which a trainer sells the rank to a
/// player who lacks the talent and a respec leaves the purchased rank behind.
/// </summary>
public sealed class TalentRankChainTrainerTests
{
    private const uint Pyroblast1 = 11366;
    private const uint Pyroblast2 = 12505;
    private const uint Pyroblast3 = 12522;

    private static SkillLineAbilityRecord[] Rows(params uint[] spells) =>
        [.. spells.Select((s, i) => new SkillLineAbilityRecord((uint)(i + 1), 8, s, 0, 0, 0, 0, 0, 0, 0))];

    [Fact]
    public void SkillLineCatalogLinksTalentAbilityRanksTheDbcLacks()
    {
        var catalog = new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2, Pyroblast3));

        Assert.Equal(Pyroblast1, catalog.PreviousRank(Pyroblast2));
        Assert.Equal(Pyroblast2, catalog.PreviousRank(Pyroblast3));
        Assert.Equal(0u, catalog.PreviousRank(Pyroblast1));
    }

    [Fact]
    public void RankChainsLinkTalentAbilityRanksForTheTrainerState()
    {
        var chains = new SpellRankChains(Rows(Pyroblast1, Pyroblast2, Pyroblast3));

        Assert.Equal(Pyroblast1, chains.Previous(Pyroblast2));
        Assert.Equal(Pyroblast1, chains.First(Pyroblast3));
        Assert.Equal(3, chains.Rank(Pyroblast3));
    }

    [Fact]
    public void RespecRankChainSeesThePurchasedHigherRank()
    {
        var chain = new SkillLineRankChain(new SkillLineAbilityCatalog(Rows(Pyroblast1, Pyroblast2)));

        Assert.Equal(Pyroblast1, chain.PreviousRank(Pyroblast2));
    }

    [Fact]
    public void DbcForwardLinkWinsOverTheSupplement()
    {
        // A DBC row says 12505 follows 99999; the supplement (11366 -> 12505) must not override it.
        var catalog = new SkillLineAbilityCatalog(
        [
            new SkillLineAbilityRecord(1, 8, 99999, 0, 0, 0, Pyroblast2, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, Pyroblast1, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(3, 8, Pyroblast2, 0, 0, 0, 0, 0, 0, 0),
        ]);

        Assert.Equal(99999u, catalog.PreviousRank(Pyroblast2));
    }

    [Fact]
    public void SupplementNeedsBothRanksToBeAbilities()
    {
        var catalog = new SkillLineAbilityCatalog(Rows(Pyroblast2));

        Assert.Equal(0u, catalog.PreviousRank(Pyroblast2));
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos SpellMgr::SelectAuraRankForLevel and its users: the cast handler's auto-downrank, SPELL_FAILED_LOWLEVEL, the group-buff
/// target skip, and the party area aura's per-member rank.</summary>
public sealed class AuraRankSelectionTests
{
    private const uint BuffR1 = 945001, BuffR2 = 945002, AuraR1 = 945011, AuraR2 = 945012;

    private static SpellInfo Ranked(uint id, uint spellLevel, SpellEffectInfo effect) => Spell(id, effect) with
    {
        SpellLevel = spellLevel,
        Duration = new SpellDuration(60_000, 0, 60_000),
        RangeIndex = 4,
        Range = new SpellRange(0, 40),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(
            Ranked(BuffR1, 1, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitFriend, AuraType.ModStat, misc: -1)),
            Ranked(BuffR2, 40, Effect(SpellEffectName.ApplyAura, 50, SpellImplicitTarget.UnitFriend, AuraType.ModStat, misc: -1)),
            Ranked(AuraR1, 1, Effect(SpellEffectName.ApplyAreaAuraParty, 5, aura: AuraType.ModResistance, misc: 1) with { Radius = 30 }) with { Duration = new SpellDuration(-1, 0, -1) },
            Ranked(AuraR2, 40, Effect(SpellEffectName.ApplyAreaAuraParty, 50, aura: AuraType.ModResistance, misc: 1) with { Radius = 30 }) with { Duration = new SpellDuration(-1, 0, -1) });
        kit.System.RankChains = new SpellRankChains(
        [
            new SkillLineAbilityRecord(1, 8, BuffR1, 0, 0, 0, BuffR2, 0, 0, 0),
            new SkillLineAbilityRecord(2, 8, BuffR2, 0, 0, 0, 0, 0, 0, 0),
            new SkillLineAbilityRecord(3, 8, AuraR1, 0, 0, 0, AuraR2, 0, 0, 0),
            new SkillLineAbilityRecord(4, 8, AuraR2, 0, 0, 0, 0, 0, 0, 0),
        ]);
        return kit;
    }

    [Fact]
    public void SelectAuraRankForLevel_TakesTheHighestRankWithinTenLevels()
    {
        using SpellTestKit kit = Kit();
        SpellInfo r2 = kit.Store.Get(BuffR2)!;

        Assert.Same(r2, kit.System.SelectAuraRankForLevel(r2, 30));
        Assert.Equal(BuffR1, kit.System.SelectAuraRankForLevel(r2, 29)!.Id);
        Assert.Equal(BuffR1, kit.System.SelectAuraRankForLevel(r2, 1)!.Id);
    }

    [Fact]
    public void CastRequest_OnALowLevelTarget_CastsTheRankItMayHold()
    {
        using SpellTestKit kit = Kit();
        (Player priest, _) = kit.AddPlayer(1);
        (Player lowbie, _) = kit.AddPlayer(2, 5, 0);
        priest.Level = 60;
        lowbie.Level = 10;
        kit.Spellbook.Teach(priest, BuffR1, BuffR2);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(priest, BuffR2, SpellCastTargets.ForUnit(lowbie.Guid)));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)

        Assert.True(kit.System.HasAura(lowbie, BuffR1));
        Assert.False(kit.System.HasAura(lowbie, BuffR2));
    }

    [Fact]
    public void DirectCast_OfATooHighRank_FailsLowLevel()
    {
        using SpellTestKit kit = Kit();
        (Player priest, _) = kit.AddPlayer(1);
        (Player lowbie, _) = kit.AddPlayer(2, 5, 0);
        priest.Level = 60;
        lowbie.Level = 10;

        Assert.Equal(SpellCastResult.Lowlevel, kit.System.CastSpell(priest, BuffR2, SpellCastTargets.ForUnit(lowbie.Guid), triggered: false));
        Assert.False(kit.System.HasAura(lowbie, BuffR2));
    }

    [Fact]
    public void PartyAreaAura_GivesALowLevelMemberTheLowerRankWithItsValue()
    {
        using SpellTestKit kit = Kit();
        var groups = new FakeGroups();
        kit.System.Groups = groups;
        (Player paladin, _) = kit.AddPlayer(1);
        (Player lowbie, _) = kit.AddPlayer(2, 5, 0);
        (Player peer, _) = kit.AddPlayer(3, 0, 5);
        paladin.Level = 60;
        lowbie.Level = 10;
        peer.Level = 60;
        groups.Parties.Add([paladin.Guid, lowbie.Guid, peer.Guid]);

        kit.System.CastSpell(paladin, AuraR2, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        Assert.True(kit.System.HasAura(peer, AuraR2));
        Assert.True(kit.System.HasAura(lowbie, AuraR1));
        Assert.False(kit.System.HasAura(lowbie, AuraR2));
        SpellAura aura = Assert.Single(kit.System.GetAuras(lowbie)).AuraSpan.ToArray().First(a => a is not null)!;
        Assert.Equal(5, aura.Amount);
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Procs.Talents;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Procs.TalentProcRig;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The item and set-bonus cases of vmangos <c>Unit::HandleDummyAuraProc</c> (UnitAuraProcHandler.cpp:680-1128) that a bare DUMMY aura would only
/// count as a charge: the Zandalarian trinket stacks, the T2/T3 set heals and refunds, Clean Escape and the class-keyed Holy/Totemic Power buffs.
/// </summary>
public sealed class ItemDummyProcTests
{
    private const uint UnstablePowerBuff = 24659;
    private const uint Heal = 996_001;
    private const uint VanishSpell = 996_002;

    private static TalentProcRig NewRig() => new(
        Talent(ItemDummyProc.UnstablePower, AuraType.Dummy, 0, ProcFlags.DealHarmfulSpell | ProcFlags.DealHelpfulSpell, charges: 12),
        Talent(UnstablePowerBuff, AuraType.ModDamageDone, 17, ProcFlags.None) with { StackAmount = 12 },
        Talent(ItemDummyProc.OracleHealingBonus, AuraType.Dummy, 0, ProcFlags.DealHelpfulSpell),
        Probe(26170, atEnemy: false),
        Talent(ItemDummyProc.DreamwalkerHealingTouch, AuraType.Dummy, 0, ProcFlags.DealHelpfulSpell),
        Probe(28742, atEnemy: false),
        Talent(ItemDummyProc.HolyPower, AuraType.Dummy, 0, ProcFlags.DealHelpfulSpell, family: 10),
        Probe(28795, atEnemy: false),
        Probe(28790, atEnemy: false),
        Talent(ItemDummyProc.CleanEscape, AuraType.Dummy, 0, ProcFlags.DealHelpfulSpell, family: 8),
        Probe(23583, atEnemy: false),
        SpellTestKit.Spell(Heal, SpellTestKit.Effect(SpellEffectName.Heal, 500, SpellImplicitTarget.UnitCaster)) with
        {
            ManaCost = 400,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        SpellTestKit.Spell(VanishSpell, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        });

    private static ProcEvent Healed(Unit target, SpellInfo spell, uint amount) => new()
    {
        Victim = target,
        AttackerFlags = ProcFlags.DealHelpfulSpell,
        VictimFlags = ProcFlags.None,
        Extra = ProcFlagsEx.NormalHit,
        Amount = amount,
        OriginalAmount = amount,
        ProcSpell = spell,
    };

    [Fact]
    public void UnstablePower_TakesOneStackOfTheBuffPerProc()
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player ally = rig.AddPlayer(2, 3, 0);
        for (int i = 0; i < 3; i++)
        {
            rig.Apply(priest, UnstablePowerBuff);
        }

        rig.Apply(priest, ItemDummyProc.UnstablePower);
        SpellAuraHolder buff = rig.System.GetAuras(priest).Single(h => h.Spell.Id == UnstablePowerBuff);
        Assert.Equal(3, buff.StackAmount);

        rig.System.ProcDamageAndSpell(priest, Healed(ally, rig.Kit.Store.Get(Heal)!, 500));

        Assert.Equal(2, buff.StackAmount);
        Assert.Equal(11, rig.System.GetAuras(priest).Single(h => h.Spell.Id == ItemDummyProc.UnstablePower).Charges);
    }

    [Fact]
    public void OracleHealingBonus_HealsTheCasterForTenPercent()
    {
        using TalentProcRig rig = NewRig();
        Player priest = rig.AddPlayer(1, 0, 0);
        Player ally = rig.AddPlayer(2, 3, 0);
        rig.Apply(priest, ItemDummyProc.OracleHealingBonus);

        rig.System.ProcDamageAndSpell(priest, Healed(ally, rig.Kit.Store.Get(Heal)!, 850));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((26170u, priest, priest, 85), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void DreamwalkerHealingTouch_RefundsThirtyPercentOfTheManaCost()
    {
        using TalentProcRig rig = NewRig();
        Player druid = rig.AddPlayer(1, 0, 0);
        Player ally = rig.AddPlayer(2, 3, 0);
        rig.Apply(druid, ItemDummyProc.DreamwalkerHealingTouch);

        rig.System.ProcDamageAndSpell(druid, Healed(ally, rig.Kit.Store.Get(Heal)!, 500));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((28742u, druid, 120), (hit.SpellId, hit.Target, hit.Value)); // 400 x 30 / 100
    }

    [Fact]
    public void HolyPower_PicksTheBuffByTheHealedTargetsClass()
    {
        using TalentProcRig rig = NewRig();
        Player paladin = rig.AddPlayer(1, 0, 0);
        Player warrior = rig.AddPlayer(2, 3, 0);
        warrior.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        rig.Apply(paladin, ItemDummyProc.HolyPower);

        rig.System.ProcDamageAndSpell(paladin, Healed(warrior, rig.Kit.Store.Get(Heal)!, 500));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal(28790u, hit.SpellId); // warrior: armor
        Assert.Equal(28795u, ItemDummyProc.ClassBuff(holyPower: true, Class.Priest));
        Assert.Equal(28826u, ItemDummyProc.ClassBuff(holyPower: false, Class.Rogue));
    }

    [Fact]
    public void CleanEscape_FiresFromVanish()
    {
        using TalentProcRig rig = NewRig();
        Player rogue = rig.AddPlayer(1, 0, 0);
        rig.Apply(rogue, ItemDummyProc.CleanEscape);

        rig.System.ProcDamageAndSpell(rogue, Healed(rogue, rig.Kit.Store.Get(VanishSpell)!, 1));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((23583u, rogue), (hit.SpellId, hit.Target));
    }
}

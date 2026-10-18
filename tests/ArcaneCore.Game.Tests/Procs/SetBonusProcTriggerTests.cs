using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Procs.TalentProcRig;

namespace ArcaneCore.Game.Tests.Procs;

/// <summary>
/// The set-bonus cases of vmangos <c>Unit::HandleProcTriggerSpellAuraProc</c> whose Spell.dbc trigger (18350, a dummy) is not what they cast:
/// The Ten Storms Lightning Shield (23551 -> 23552 on the healed target, 23552 -> 27635), The Earthfury Mana Surge (23572 -> 23571 with 35% of the
/// heal's mana cost) and Cheat Death's 20% health gate (28845); and the Earthshatterer aura (28820) that removes itself without a Lightning Shield.
/// </summary>
public sealed class SetBonusProcTriggerTests
{
    private const uint ShamanFamily = 11;
    private const uint DummyTrigger = 18350;
    private const uint HealingWave = 331;

    private static SpellInfo FriendProbe(uint id) => SpellTestKit.Spell(id, SpellTestKit.Effect(ProbeEffect, 1, SpellImplicitTarget.UnitFriend)) with
    {
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
        RangeIndex = 4,
        Range = new SpellRange(0, 100),
    };

    private static TalentProcRig NewRig() => new(
        Talent(23551, AuraType.ProcTriggerSpell, 5, ProcFlags.DealHelpfulSpell, ShamanFamily, icon: 19, trigger: DummyTrigger),
        FriendProbe(23552),
        Talent(23572, AuraType.ProcTriggerSpell, 1, ProcFlags.DealHelpfulSpell, ShamanFamily, icon: 87, trigger: DummyTrigger),
        Probe(23571, atEnemy: false),
        Talent(28845, AuraType.ProcTriggerSpell, 100, ProcFlags.TakeMeleeSwing, 5, icon: 1, trigger: 28846),
        Probe(28846, atEnemy: false),
        SpellTestKit.Spell(HealingWave, SpellTestKit.Effect(SpellEffectName.Heal, 100, SpellImplicitTarget.UnitFriend)) with
        {
            SpellFamilyName = ShamanFamily, SpellFamilyFlags = 0x40, ManaCost = 200, DamageClass = SpellDamageClass.Magic,
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        });

    private static ProcEvent Heal(Unit target, SpellInfo spell) => new()
    {
        Victim = target,
        AttackerFlags = ProcFlags.DealHelpfulSpell,
        VictimFlags = ProcFlags.TakeHelpfulSpell,
        Extra = ProcFlagsEx.NormalHit,
        Amount = 500,
        OriginalAmount = 500,
        ProcSpell = spell,
    };

    private static ProcEvent Struck(Unit victim, uint amount) => new()
    {
        Victim = victim,
        AttackerFlags = ProcFlags.DealMeleeSwing,
        VictimFlags = ProcFlags.TakeMeleeSwing | ProcFlags.TakenAnyDamage,
        Extra = ProcFlagsEx.NormalHit,
        Amount = amount,
        OriginalAmount = amount,
    };

    [Fact]
    public void TheTenStorms_GivesTheHealedTargetAFreeLightningShield()
    {
        using TalentProcRig rig = NewRig();
        Player shaman = rig.AddPlayer(1, 0, 0);
        Player friend = rig.AddPlayer(2, 3, 0);
        rig.Apply(shaman, 23551);

        rig.System.ProcDamageAndSpell(shaman, Heal(friend, rig.Kit.Store.Get(HealingWave)!));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((23552u, shaman, friend), (hit.SpellId, hit.Caster, hit.Target));
    }

    [Fact]
    public void ManaSurge_Returns35PercentOfTheHealsManaCost()
    {
        using TalentProcRig rig = NewRig();
        Player shaman = rig.AddPlayer(1, 0, 0);
        Player friend = rig.AddPlayer(2, 3, 0);
        rig.Apply(shaman, 23572);

        rig.System.ProcDamageAndSpell(shaman, Heal(friend, rig.Kit.Store.Get(HealingWave)!));

        TalentProcRig.ProbeHit hit = Assert.Single(rig.Probes);
        Assert.Equal((23571u, shaman, shaman, 70), (hit.SpellId, hit.Caster, hit.Target, hit.Value));
    }

    [Fact]
    public void CheatDeath_ProcsOnTheHitThatDropsBelowTwentyPercent_ButNotWhenAlreadyBelow()
    {
        using TalentProcRig rig = NewRig();
        Player rogue = rig.AddPlayer(1, 0, 0);
        rogue.MaxHealth = 1_000;
        rig.Apply(rogue, 28845);

        rogue.Health = 150; // already under 200
        rig.System.ProcDamageAndSpell(rogue, Struck(rogue, 50));
        Assert.Empty(rig.Probes);

        rogue.Health = 250; // 250 - 100 = 150 < 200
        rig.System.ProcDamageAndSpell(rogue, Struck(rogue, 100));
        Assert.Equal(28846u, Assert.Single(rig.Probes).SpellId);
    }

    [Fact]
    public void Earthshatterer_RemovesItselfOnATickWithoutLightningShield()
    {
        var aura = SpellTestKit.Spell(ShamanSetBonusModule.EarthshattererLightningShield,
            SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.PeriodicTriggerSpell, amplitude: 5000, trigger: DummyTrigger)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        };
        var shield = SpellTestKit.Spell(324, SpellTestKit.Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
        {
            SpellFamilyName = ShamanFamily, SpellFamilyFlags = 1UL << 10, SpellVisual = 1,
            Duration = new SpellDuration(600_000, 0, 600_000),
        };
        using var kit = new SpellTestKit(aura, shield);
        (Player shaman, _) = kit.AddPlayer(1);
        Assert.True(kit.System.AddAura(shaman, 324));
        Assert.True(kit.System.AddAura(shaman, ShamanSetBonusModule.EarthshattererLightningShield));

        kit.Advance(5_100);
        Assert.True(kit.System.HasAura(shaman, ShamanSetBonusModule.EarthshattererLightningShield));

        kit.System.RemoveAuras(shaman, 324);
        kit.Advance(5_100);
        Assert.False(kit.System.HasAura(shaman, ShamanSetBonusModule.EarthshattererLightningShield));
    }
}
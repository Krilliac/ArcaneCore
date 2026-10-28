using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Procs;

/// <summary>
/// SpellSystem.TriggerDamageShields (vmangos Unit::TriggerDamageShields, Unit.cpp:1785-1849) runs on every landed melee hit. A victim without a
/// damage shield must cost nothing (no allocation), and a victim with shields must fire each exactly once, in aura order, with the miss, immunity
/// and attacker-death rules intact.
/// </summary>
public sealed class DamageShieldFastPathTests
{
    private const uint ShieldA = 993_001; // 10 Nature
    private const uint ShieldB = 993_002; // 30 Fire
    private const uint ShieldC = 993_003; // 50 Frost
    private const uint DoubleShield = 993_004; // two DamageShield effects: 7 and 9 Nature
    private const uint Filler1 = 993_010;
    private const uint Filler2 = 993_011;
    private const uint Filler3 = 993_012;
    private const uint FireImmune = 993_020;

    private static SpellInfo Permanent(SpellInfo spell) => spell with
    {
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Shield(uint id, int amount, SpellSchool school) =>
        Permanent(Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.DamageShield))) with { School = school };

    private static SpellTestKit NewKit() => new(
        Shield(ShieldA, 10, SpellSchool.Nature),
        Shield(ShieldB, 30, SpellSchool.Fire),
        Shield(ShieldC, 50, SpellSchool.Frost),
        Permanent(Spell(DoubleShield,
            Effect(SpellEffectName.ApplyAura, 7, aura: AuraType.DamageShield),
            Effect(SpellEffectName.ApplyAura, 9, aura: AuraType.DamageShield))) with { School = SpellSchool.Nature },
        RuleTestKit.Grant(Filler1, AuraType.ModResistance, 5, misc: 1),
        RuleTestKit.Grant(Filler2, AuraType.ModResistance, 3, misc: 2),
        RuleTestKit.Grant(Filler3, AuraType.ModStat, 2, misc: 0),
        RuleTestKit.Grant(FireImmune, AuraType.SchoolImmunity, 0, misc: 1 << (int)SpellSchool.Fire)
            with { AttributesEx = (SpellAttributesEx)SpellRuleFlags.ExImmunityToHostileAndFriendly }); // the shield spells are positive, so the immunity must cover friendly polarity

    private sealed record Rig(SpellTestKit Kit, Player Attacker, Player Victim, FakeSession Observer) : IDisposable
    {
        public void Dispose() => Kit.Dispose();
    }

    private static Rig NewRig(uint attackerHealth = 1000)
    {
        SpellTestKit kit = NewKit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        (_, FakeSession observer) = kit.AddPlayer(3, 1);
        attacker.Health = attacker.MaxHealth = attackerHealth;
        victim.Health = victim.MaxHealth = 1000;
        return new Rig(kit, attacker, victim, observer);
    }

    /// <summary>Apply a self aura a millisecond early, as the existing proc tests do (an aura applied at the event's own time does not proc).</summary>
    private static void Apply(Rig rig, uint id)
    {
        RuleTestKit.Apply(rig.Kit, rig.Victim, id);
        rig.Kit.Now++;
    }

    private static List<(uint Damage, uint School)> ShieldPackets(Rig rig)
    {
        var result = new List<(uint, uint)>();
        foreach (byte[] payload in Packets(rig.Observer, WorldOpcode.SmsgSpelldamageshield))
        {
            var reader = new PacketReader(payload);
            Assert.Equal(rig.Victim.Guid.Value, reader.ReadUInt64());
            Assert.Equal(rig.Attacker.Guid.Value, reader.ReadUInt64());
            result.Add((reader.ReadUInt32(), reader.ReadUInt32()));
        }

        return result;
    }

    private sealed class MissingRules(params uint[] missing) : ISpellCombatRules
    {
        public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell)
            => missing.Contains(spell.Id) ? SpellMissInfo.Miss : SpellMissInfo.None;

        public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => false;

        public float CritMultiplier(SpellInfo spell) => 1.0f;

        public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => 0;

        public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage;
    }

    // ---- A. allocation ----

    [Fact]
    public void TriggerDamageShields_VictimWithOnlyOtherAuras_AllocatesNothing()
    {
        using Rig rig = NewRig();
        Apply(rig, Filler1);
        Apply(rig, Filler2);
        Apply(rig, Filler3);
        SpellSystem system = rig.Kit.System;
        Unit attacker = rig.Attacker, victim = rig.Victim;
        Action window = () =>
        {
            for (int i = 0; i < 100; i++) system.TriggerDamageShields(attacker, victim);
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "TriggerDamageShields on a victim with three non-shield auras");
        Assert.Equal(1000u, attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_VictimWithNoAuras_AllocatesNothing()
    {
        using Rig rig = NewRig();
        SpellSystem system = rig.Kit.System;
        Unit attacker = rig.Attacker, victim = rig.Victim;
        Action window = () =>
        {
            for (int i = 0; i < 100; i++) system.TriggerDamageShields(attacker, victim);
        };

        AllocationProbe.AssertEveryCleanWindowAtMost(window, 0, "TriggerDamageShields on a victim with no auras");
        Assert.Equal(1000u, attacker.Health);
    }

    // ---- B. behaviour preservation ----

    [Fact]
    public void TriggerDamageShields_SingleShield_HurtsAttackerOnceAndSendsOnePacketWithSchoolAndAmount()
    {
        using Rig rig = NewRig();
        Apply(rig, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.Equal(990u, rig.Attacker.Health);
        Assert.Equal([(10u, (uint)SpellSchool.Nature)], ShieldPackets(rig));
    }

    [Fact]
    public void TriggerDamageShields_TwoHolders_FireOnceEachInApplicationOrder()
    {
        using Rig rig = NewRig();
        Apply(rig, ShieldB);
        Apply(rig, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.Equal([(30u, (uint)SpellSchool.Fire), (10u, (uint)SpellSchool.Nature)], ShieldPackets(rig));
        Assert.Equal(960u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_ThreeHolders_FireInApplicationOrder()
    {
        using Rig rig = NewRig();
        Apply(rig, ShieldA);
        Apply(rig, ShieldB);
        Apply(rig, ShieldC);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.Equal([(10u, (uint)SpellSchool.Nature), (30u, (uint)SpellSchool.Fire), (50u, (uint)SpellSchool.Frost)], ShieldPackets(rig));
        Assert.Equal(910u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_OneHolderWithTwoShieldEffects_FiresEachEffectOnceInEffectOrder()
    {
        using Rig rig = NewRig();
        Apply(rig, DoubleShield);
        Apply(rig, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.Equal([(7u, (uint)SpellSchool.Nature), (9u, (uint)SpellSchool.Nature), (10u, (uint)SpellSchool.Nature)], ShieldPackets(rig));
        Assert.Equal(974u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_MissedShield_SendsSpellLogMissAndTheNextShieldStillFires()
    {
        using Rig rig = NewRig();
        rig.Kit.System.CombatRules = new MissingRules(ShieldA);
        Apply(rig, ShieldA);
        Apply(rig, ShieldB);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        var miss = new PacketReader(Assert.Single(Packets(rig.Observer, WorldOpcode.SmsgSpelllogmiss)));
        Assert.Equal(ShieldA, miss.ReadUInt32());
        Assert.Equal(rig.Victim.Guid.Value, miss.ReadUInt64());
        Assert.Equal([(30u, (uint)SpellSchool.Fire)], ShieldPackets(rig));
        Assert.Equal(970u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_AttackerImmuneToTheSchool_SendsImmuneAndNoDamageButNextShieldFires()
    {
        using Rig rig = NewRig();
        RuleTestKit.Apply(rig.Kit, rig.Attacker, FireImmune);
        Apply(rig, ShieldB); // fire: immune
        Apply(rig, ShieldA); // nature: lands
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        var immune = new PacketReader(Assert.Single(Packets(rig.Observer, WorldOpcode.SmsgSpellordamageImmune)));
        Assert.Equal(rig.Victim.Guid.Value, immune.ReadUInt64());
        Assert.Equal(rig.Attacker.Guid.Value, immune.ReadUInt64());
        Assert.Equal(ShieldB, immune.ReadUInt32());
        Assert.Equal([(10u, (uint)SpellSchool.Nature)], ShieldPackets(rig));
        Assert.Equal(990u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_ShieldKillsTheAttacker_StopsFurtherShields()
    {
        using Rig rig = NewRig(attackerHealth: 40);
        Apply(rig, ShieldC); // 50 frost kills a 40 health attacker
        Apply(rig, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.False(rig.Attacker.IsAlive);
        Assert.Equal([(50u, (uint)SpellSchool.Frost)], ShieldPackets(rig));
    }

    [Fact]
    public void TriggerDamageShields_RemovedHolder_IsSkipped()
    {
        using Rig rig = NewRig();
        Apply(rig, ShieldA);
        Apply(rig, ShieldB);
        rig.Kit.System.RemoveAuras(rig.Victim, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Attacker, rig.Victim);

        Assert.Equal([(30u, (uint)SpellSchool.Fire)], ShieldPackets(rig));
        Assert.Equal(970u, rig.Attacker.Health);
    }

    [Fact]
    public void TriggerDamageShields_AttackerEqualsVictim_DoesNothing()
    {
        using Rig rig = NewRig();
        Apply(rig, ShieldA);
        rig.Observer.Clear();

        rig.Kit.System.TriggerDamageShields(rig.Victim, rig.Victim);

        Assert.Empty(ShieldPackets(rig));
        Assert.Equal(1000u, rig.Victim.Health);
    }
}

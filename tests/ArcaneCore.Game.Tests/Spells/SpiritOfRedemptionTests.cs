using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spirit of Redemption (docs/areas/unit-control.md): vmangos Unit::Kill (Unit.cpp:1108-1140), Unit::DealDamage's invincibility threshold
/// (Unit.cpp:825-850), Aura::HandleSpiritOfRedemption (SpellAuras.cpp:5699-5738) and the form's linked spells (SpellAuras.cpp:5480-5483). The
/// spells are the classic-db rows reduced to what the chain needs: 20711 (talent, dummy aura), 27827 (heal for the custom amount, water
/// breathing, shapeshift form 32, 10 s), 27792 (unattackable, root), 27795 (pacify, SPIRIT_OF_REDEMPTION, no regeneration), 27965 (Suicide,
/// instakill), 25100 (Untransform Hero, a dummy).
/// </summary>
public sealed class SpiritOfRedemptionTests
{
    private const uint Talent = 20711;
    private const uint Spirit = 27827;
    private const uint Linked1 = 27792;
    private const uint Linked2 = 27795;
    private const uint Suicide = 27965;
    private const uint Untransform = 25100;
    private const uint Bolt = 930_100;
    private const byte FormSpirit = 32;

    private static SpellInfo Permanent(SpellInfo spell) => spell with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit()
    {
        var kit = new SpellTestKit(
            Permanent(Spell(Talent, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))),
            Spell(Spirit,
                Effect(SpellEffectName.Heal, 1),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.WaterBreathing),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModShapeshift, misc: FormSpirit)) with
            {
                Duration = new SpellDuration(10_000, 0, 10_000),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Permanent(Spell(Linked1,
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModUnattackable),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModRoot))),
            Permanent(Spell(Linked2,
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModPacify),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.SpiritOfRedemption),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.InterruptRegen))),
            Spell(Suicide, Effect(SpellEffectName.Instakill, 0)),
            Spell(Untransform, Effect(SpellEffectName.Dummy, 0)),
            Spell(Bolt, Effect(SpellEffectName.SchoolDamage, 500, SpellImplicitTarget.UnitEnemy)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                School = SpellSchool.Shadow,
            });
        new ShapeshiftService(kit.System, ShapeshiftFormCatalog.Retail, new CombatOptions(), _ => []).Install();
        kit.System.Damage = new MapCombatDamageSink();
        kit.World.GetMap(0).Combat.SpellMitigation = kit.System;
        return kit;
    }

    private static (Player Priest, Player Enemy) Setup(SpellTestKit kit, bool talent)
    {
        (Player priest, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 10, 0);
        priest.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
        priest.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        priest.SetUInt32(UpdateFields.UnitFieldMaxpower1, 400);
        SpellSystem.SetPower(priest, PowerType.Mana, 10);
        priest.UnitFlags |= UnitFlags.Pvp;
        if (talent)
        {
            kit.System.CastSpell(priest, Talent, SpellCastTargets.ForSelf(), triggered: true);
        }

        return (priest, enemy);
    }

    private static void Bolt_(SpellTestKit kit, Player enemy, Player priest)
        => kit.System.CastSpell(enemy, Bolt, SpellCastTargets.ForUnit(priest.Guid), triggered: true);

    [Fact]
    public void AKillingBlow_MakesAPriestWithTheTalentASpirit_InsteadOfKillingIt()
    {
        using SpellTestKit kit = NewKit();
        (Player priest, Player enemy) = Setup(kit, talent: true);
        int kills = 0;
        kit.World.GetMap(0).Combat.UnitKilled += (_, victim) => kills += ReferenceEquals(victim, priest) ? 1 : 0;

        Bolt_(kit, enemy, priest);

        Assert.True(priest.IsAlive);
        Assert.Equal(priest.MaxHealth, priest.Health);                    // 27827 heals for the maximum health
        Assert.Equal(400u, SpellSystem.GetPower(priest, PowerType.Mana)); // and the aura fills the mana
        Assert.True(kit.System.HasAura(priest, Spirit));
        Assert.True(kit.System.HasAura(priest, Linked1));
        Assert.True(kit.System.HasAura(priest, Linked2));
        Assert.Equal(FormSpirit, priest.GetByte(UpdateFields.UnitFieldBytes1, 2));
        Assert.Equal(16031u, priest.DisplayId);
        Assert.Equal(1, kills); // the kill itself (credit, honor) happened
        Assert.True(kit.System.HasAura(priest, Talent));    // passive: kept
    }

    [Fact]
    public void TheSpirit_TakesNoDamage_AndDiesWhenTheFormEnds()
    {
        using SpellTestKit kit = NewKit();
        (Player priest, Player enemy) = Setup(kit, talent: true);
        Bolt_(kit, enemy, priest);
        uint full = priest.Health;

        Bolt_(kit, enemy, priest);
        Assert.Equal(full, priest.Health);

        kit.Advance(10_000); // 27827 ends: the form goes, and with it the spirit aura
        Assert.False(kit.System.HasAura(priest, Linked2));
        Assert.NotEqual(0u, (uint)(priest.UnitFlags & UnitFlags.Stunned));
        Assert.True(priest.IsAlive);

        kit.World.RunTick(400); // a batching interval (vmangos BATCHING_INTERVAL) later: Suicide
        Assert.False(priest.IsAlive);
        Assert.Equal(0u, priest.Health);
        Assert.Equal(0u, (uint)(priest.UnitFlags & UnitFlags.Stunned));
    }

    [Fact]
    public void TheSpirit_LosesNothingToANonLethalHit()
    {
        using SpellTestKit kit = NewKit();
        (Player priest, Player enemy) = Setup(kit, talent: true);
        priest.MaxHealth = 1000;
        priest.Health = 400;
        Bolt_(kit, enemy, priest); // 500 on 400: the spirit, healed to 1000 with a threshold of 1000
        Assert.True(kit.System.HasAura(priest, Linked2));
        Assert.Equal(1000u, priest.Health);

        // vmangos Unit::DealDamage (Unit.cpp:844-848): health above the threshold loses at most the part above it, so nothing here,
        // even though 500 on 1000 health is not a killing blow.
        Bolt_(kit, enemy, priest);
        Assert.Equal(1000u, priest.Health);
        Assert.True(priest.IsAlive);
    }

    [Fact]
    public void AnInvincibilityThreshold_ClampsEveryHit_LethalOrNot()
    {
        using SpellTestKit kit = NewKit();
        (Player priest, Player enemy) = Setup(kit, talent: false);
        priest.MaxHealth = 1000;
        priest.Health = 1000;
        priest.InvincibilityHpThreshold = 300;
        MapCombat combat = kit.World.GetMap(0).Combat;

        combat.DealDamage(enemy, priest, 800); // not lethal, but it would cross the threshold: min(1000 - 300, 800)
        Assert.Equal(300u, priest.Health);

        combat.DealDamage(enemy, priest, 800); // at the threshold: nothing
        Assert.Equal(300u, priest.Health);
        Assert.True(priest.IsAlive);

        priest.InvincibilityHpThreshold = 0;
        combat.DealDamage(enemy, priest, 100);
        Assert.Equal(200u, priest.Health);
    }

    [Fact]
    public void WithoutTheTalent_OrAgainstSuicide_ThePriestDies()
    {
        using SpellTestKit kit = NewKit();
        (Player priest, Player enemy) = Setup(kit, talent: false);

        Bolt_(kit, enemy, priest);

        Assert.False(priest.IsAlive);
        Assert.False(kit.System.HasAura(priest, Spirit));
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// Improved Drain Soul (18213/18372): Drain Soul's third effect is a PROC_TRIGGER_SPELL aura on the warlock itself, on KILL, that casts
/// Soul Siphon (18371). The talent only raises its chance (SPELLMOD_CHANCE_OF_SUCCESS). vmangos fires on-kill procs right away
/// (SpellCaster.cpp:285, "Always execute On Kill procs instantly. Fixes Improved Drain Soul talent"), so the proc has to run while the
/// channel's self aura is still there, even though the dead target's own Drain Soul debuff is already gone.
/// </summary>
public sealed class ImprovedDrainSoulTests
{
    private const uint DrainSoul = 962_101;
    private const uint SoulSiphon = 962_102;

    private static PetTestKit Kit() => new(
    [
        Spell(DrainSoul,
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.ChannelDeathItem),
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.PeriodicDamage, amplitude: 3000),
            Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.ProcTriggerSpell, trigger: SoulSiphon)) with
        {
            SpellFamilyName = 5,
            Duration = new SpellDuration(15_000, 0, 15_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            AttributesEx = SpellAttributesEx.IsChanneled,
            ProcFlags = ProcFlags.Kill,
            ProcChance = 100,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
        Spell(SoulSiphon, Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitCaster, AuraType.ModPowerRegenPercent)) with
        {
            Duration = new SpellDuration(1_000, 0, 1_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ]);

    private static int _summoners;

    private static Creature Victim(PetTestKit kit)
    {
        (Player summoner, _) = kit.AddPlayer((uint)(900 + _summoners++));
        int before = kit.Creatures.Creatures.Count();
        kit.Cast(summoner, WildSpell);
        return kit.Creatures.Creatures.Skip(before).First();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheTargetDyingUnderDrainSoul_ProcsSoulSiphon(bool deathFirst)
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        warlock.Level = 5;
        Creature victim = Victim(kit);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(warlock, DrainSoul, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        kit.Spells.Advance(100);
        Assert.True(kit.Spells.System.HasAura(warlock, DrainSoul));

        victim.Health = 0;
        if (deathFirst)
        {
            kit.Spells.System.OnUnitDied(victim);
            kit.Spells.System.OnUnitKilled(warlock, victim);
        }
        else
        {
            kit.Spells.System.OnUnitKilled(warlock, victim);
            kit.Spells.System.OnUnitDied(victim);
        }

        Assert.True(kit.Spells.System.HasAura(warlock, SoulSiphon));
    }
}
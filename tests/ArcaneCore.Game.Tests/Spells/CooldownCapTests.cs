using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.ClassScripts;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A recovery above 2^31 ms is capped at <see cref="SpellSystem.MaxReasonableCooldownMs"/> on the cast path (it would otherwise wrap
/// the signed compare and read as expired), and an Orc Blood Fury recast within the debuff's 25 s applies the buff and refreshes the
/// debuff again (vmangos SpellEffects.cpp:971-985 applies both on every cast).
/// </summary>
public sealed class CooldownCapTests
{
    private const uint HugeSpell = 946101;

    [Fact]
    public void ARecoveryAbove2To31Ms_IsCapped_AndStaysOnCooldown()
    {
        using var kit = new SpellTestKit(
            Spell(HugeSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster))
                with { RecoveryTime = 3_000_000_000u, StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        kit.System.SpellBatchWindowMs = 0;
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, HugeSpell);
        uint castAt = kit.Now;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, HugeSpell, SpellCastTargets.ForSelf(), triggered: false));

        uint until = kit.System.GetState(player.Guid)!.SpellCooldowns[HugeSpell];
        Assert.Equal(SpellSystem.MaxReasonableCooldownMs, until - castAt);
        kit.Now += 1_000;
        Assert.False(kit.System.IsSpellReady(player, kit.Store.Get(HugeSpell)!));
    }

    [Fact]
    public void BloodFuryRecastAfterAReset_AppliesTheBuffAndRefreshesTheDebuff()
    {
        SpellInfo bloodFury = Spell(BloodFuryScript.BloodFury, Effect(SpellEffectName.Dummy, 25));
        SpellInfo buff = Spell(BloodFuryScript.BloodFuryBuff, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModAttackPower)) with
        {
            Duration = new SpellDuration(15_000, 0, 15_000),
        };
        SpellInfo debuff = Spell(BloodFuryScript.BloodFuryDebuff, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModHealingPct)) with
        {
            Duration = new SpellDuration(25_000, 0, 25_000),
        };

        using var kit = new SpellTestKit(bloodFury, buff, debuff);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(BloodFuryScript).Assembly));
        (Player player, _) = kit.AddPlayer(1, race: Race.Orc);
        player.SetInt32(UpdateFields.UnitFieldAttackPower, 400);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, BloodFuryScript.BloodFury, SpellCastTargets.ForSelf(), triggered: true));
        kit.Advance(10_000);
        int debuffLeft = kit.System.GetAuras(player).Single(h => h.Spell.Id == BloodFuryScript.BloodFuryDebuff).Duration;
        kit.System.RemoveAuras(player, BloodFuryScript.BloodFuryBuff);
        Assert.False(kit.System.HasAura(player, BloodFuryScript.BloodFuryBuff));

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, BloodFuryScript.BloodFury, SpellCastTargets.ForSelf(), triggered: true));

        Assert.True(kit.System.HasAura(player, BloodFuryScript.BloodFuryBuff));
        Assert.Equal(100, kit.System.GetTotalAuraModifier(player, AuraType.ModAttackPower));
        Assert.True(kit.System.GetAuras(player).Single(h => h.Spell.Id == BloodFuryScript.BloodFuryDebuff).Duration > debuffLeft);
    }
}

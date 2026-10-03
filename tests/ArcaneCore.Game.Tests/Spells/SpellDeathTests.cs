using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Death removes non-passive auras (SpellSystem.OnUnitDied) and the death state is what persistence then sees.</summary>
public sealed class SpellDeathTests
{
    private const uint Root = 900600;
    private const uint Buff = 900601;
    private const long Saved = 1_800_000_000_000;

    [Fact]
    public void Death_RemovesStunRootDotAndBuff_KeepsPassive_AndKeepsTheDeathRoot()
    {
        using var kit = Kit();
        (Player victim, FakeSession session) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(enemy, StunSpell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        kit.System.CastSpell(enemy, DotSpell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        kit.System.CastSpell(victim, Root, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, Buff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, Passive, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(5, kit.System.GetAuras(victim).Count);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.True(victim.IsRooted);

        victim.Map!.FindUpdater<MapCombat>()!.Kill(enemy, victim);
        session.Clear();
        kit.System.OnUnitDied(victim);

        SpellAuraHolder left = Assert.Single(kit.System.GetAuras(victim));
        Assert.Equal(Passive, left.Spell.Id);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) == 0);
        Assert.True(victim.IsRooted); // MapCombat's JUST_DIED root survives the loss of the root/stun auras
        Assert.Empty(Packets(session, WorldOpcode.SmsgForceMoveUnroot));
        for (int slot = 0; slot < MaxAuraSlots; slot++)
        {
            Assert.Equal(0u, victim.GetUInt32(UpdateFields.UnitFieldAura + slot));
        }
    }

    [Fact]
    public void Death_ThenCapture_SavesCooldownsButNoAuras_AndRestoreBringsNothingBack()
    {
        using var kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, Buff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(enemy, DotSpell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Equal(2, kit.System.CaptureState(victim, Saved).Auras.Count);

        victim.Map!.FindUpdater<MapCombat>()!.Kill(enemy, victim);
        kit.System.OnUnitDied(victim);
        SpellStateSnapshot state = kit.System.CaptureState(victim, Saved);

        Assert.Empty(state.Auras);
        Assert.Equal(CooldownSpell, Assert.Single(state.Cooldowns).Id);
        kit.System.RemoveUnit(victim);
        kit.World.RemovePlayer(victim);
        (Player again, _) = kit.AddPlayer(1);
        Assert.Empty(kit.System.RestoreAuras(again, state.Auras, Saved + 1_000));
        Assert.Empty(kit.System.GetAuras(again));
    }

    [Fact]
    public void Death_StopsTheDotFromTicking()
    {
        using var kit = Kit();
        (Player victim, FakeSession session) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(enemy, DotSpell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        victim.Map!.FindUpdater<MapCombat>()!.Kill(enemy, victim);
        kit.System.OnUnitDied(victim);
        session.Clear();

        kit.Advance(9000);

        Assert.Empty(Packets(session, WorldOpcode.SmsgPeriodicauralog));
        Assert.Empty(kit.System.GetAuras(victim));
    }

    [Fact]
    public void OnUnitDied_IsANoOp_ForALivingUnit()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Buff, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.OnUnitDied(player);

        Assert.Single(kit.System.GetAuras(player));
    }

    private const int MaxAuraSlots = SpellSystem.MaxAuras;

    private static SpellTestKit Kit() => new(
        Spell(Root, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModRoot))
            with { Duration = new SpellDuration(10_000, 0, 10_000), SpellVisual = 1, StartRecoveryCategory = 0 },
        Spell(Buff, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0 });
}

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

    [Fact]
    public void Death_KeepsDeathPersistentAuras_ButRemovesTheOrdinaryOnes()
    {
        using var kit = Kit();
        (Player victim, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        kit.System.CastSpell(victim, Persistent, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(victim, Buff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(2, kit.System.GetAuras(victim).Count);

        victim.Map!.FindUpdater<MapCombat>()!.Kill(enemy, victim);
        kit.System.OnUnitDied(victim);

        Assert.Equal(Persistent, Assert.Single(kit.System.GetAuras(victim)).Spell.Id);
    }

    [Theory]
    [InlineData(SlowBuff, false)]
    [InlineData(SlowDeadTargetOk, true)]
    public void ApplyAura_LandingOnATargetThatDiedMidCast_NeedsADeadTargetSpell(uint spellId, bool applied)
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        (Player killer, _) = kit.AddPlayer(3, 2);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(target.Guid), triggered: false));
        target.Map!.FindUpdater<MapCombat>()!.Kill(killer, target);
        kit.System.OnUnitDied(target);

        kit.Advance(1500);

        Assert.Equal(applied, kit.System.GetAuras(target).Any(h => h.Spell.Id == spellId));
    }

    [Theory]
    [InlineData(GhostBuff, false)]
    [InlineData(GhostPersistent, true)]
    public void ApplyAura_OnADeadCaster_NeedsAPersistentSpell(uint spellId, bool applied)
    {
        using var kit = Kit();
        (Player ghost, _) = kit.AddPlayer(1);
        (Player killer, _) = kit.AddPlayer(2, 2);
        ghost.Map!.FindUpdater<MapCombat>()!.Kill(killer, ghost);
        kit.System.OnUnitDied(ghost);

        kit.System.CastSpell(ghost, spellId, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(applied, kit.System.GetAuras(ghost).Any(h => h.Spell.Id == spellId));
    }

    [Fact]
    public void Death_InterruptsTheCastInProgress_SoItNeverCompletes()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        (Player killer, _) = kit.AddPlayer(3, 2);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        uint health = target.Health;

        caster.Map!.FindUpdater<MapCombat>()!.Kill(killer, caster);
        kit.System.OnUnitDied(caster);

        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast); // interrupted at death, not left to time out
        kit.Advance(2500);
        Assert.Equal(health, target.Health);
    }

    [Fact]
    public void Death_EndsTheChannel_AndItsAura()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player killer, _) = kit.AddPlayer(2, 2);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, ChannelSpell, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal(SpellCastState.Casting, kit.System.GetState(caster.Guid)!.CurrentCast!.State);

        caster.Map!.FindUpdater<MapCombat>()!.Kill(killer, caster);
        kit.System.OnUnitDied(caster);

        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Empty(kit.System.GetAuras(caster));
        Assert.Equal(0u, caster.GetUInt32(UpdateFields.UnitChannelSpell));
    }

    private const uint Persistent = 900602;
    private const uint GhostBuff = 900607;
    private const uint GhostPersistent = 900608;
    private const uint SlowBuff = 900604;
    private const uint SlowDeadTargetOk = 900606;

    private const int MaxAuraSlots = SpellSystem.MaxAuras;

    private static SpellInfo Slow(uint id, uint ex3, SpellAttributesEx2 ex2) => Spell(id, Effect(SpellEffectName.ApplyAura, 15, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        CastTime = new SpellCastTime(1000, 0, 0),
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        AttributesEx3 = ex3,
        AttributesEx2 = ex2,
    };

    private static SpellTestKit Kit() => new(
        Spell(Root, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.ModRoot))
            with { Duration = new SpellDuration(10_000, 0, 10_000), SpellVisual = 1, StartRecoveryCategory = 0 },
        Spell(Buff, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0 },
        Spell(Persistent, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0, AttributesEx3 = 0x00100000 },
        Spell(GhostBuff, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0, Attributes = SpellAttributes.AllowCastWhileDead },
        Spell(GhostPersistent, Effect(SpellEffectName.ApplyAura, 15, aura: AuraType.Dummy))
            with { Duration = new SpellDuration(30_000, 0, 30_000), SpellVisual = 1, StartRecoveryCategory = 0, Attributes = SpellAttributes.AllowCastWhileDead, AttributesEx3 = 0x00100000 },
        Slow(SlowBuff, 0, 0),
        Slow(SlowDeadTargetOk, 0, SpellAttributesEx2.AllowDeadTarget));
}

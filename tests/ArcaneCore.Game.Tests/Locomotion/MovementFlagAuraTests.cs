using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// SPELL_AURA_WATER_WALK (104), FEATHER_FALL (105), HOVER (106) drive the movement changes (vmangos
/// HandleAuraWaterWalk / HandleAuraFeatherFall / HandleAuraHover, SpellAuras.cpp:2278-2303) and SAFE_FALL (144) is a
/// value read by the fall code (HandleAuraSafeFall: "implemented in WorldSession::HandleMovementOpcodes").
/// The amounts follow the 1.12 spell data (Safe Fall 1860 = 17 yards, Feline Grace 20719 = 17).
/// </summary>
public sealed class MovementFlagAuraTests
{
    private const uint WaterWalking = 940101;
    private const uint SlowFall = 940102;
    private const uint HoverSpell = 940103;
    private const uint SafeFallSpell = 940104;
    private const uint FelineGrace = 940105;
    private const uint Levitate = 940106;

    private static SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        };

        return new SpellTestKit(
            Perm(WaterWalking, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.WaterWalk)),
            Perm(SlowFall, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.FeatherFall)),
            Perm(HoverSpell, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Hover)),
            Perm(SafeFallSpell, Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.SafeFall)),
            Perm(FelineGrace, Effect(SpellEffectName.ApplyAura, 17, aura: AuraType.SafeFall)),
            // Levitate (1706) is Feather Fall + Water Walk + Hover in one spell.
            Perm(Levitate,
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.FeatherFall),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.WaterWalk),
                Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Hover)));
    }

    [Fact]
    public void TheModuleIsDiscovered_AndTheFourAuraTypesHaveHandlers()
    {
        using var kit = Kit();
        Assert.Contains(typeof(MovementFlagAuras), kit.System.Modules);
        foreach (AuraType type in new[] { AuraType.WaterWalk, AuraType.FeatherFall, AuraType.Hover, AuraType.SafeFall })
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
        }
    }

    [Theory]
    [InlineData(WaterWalking, WorldOpcode.SmsgMoveWaterWalk, WorldOpcode.SmsgMoveLandWalk, MovementFlags.WaterWalking)]
    [InlineData(SlowFall, WorldOpcode.SmsgMoveFeatherFall, WorldOpcode.SmsgMoveNormalFall, MovementFlags.SafeFall)]
    [InlineData(HoverSpell, WorldOpcode.SmsgMoveSetHover, WorldOpcode.SmsgMoveUnsetHover, MovementFlags.Hover)]
    public void TheAura_OrdersTheChange_OnApplyAndOnRemoval(uint spell, WorldOpcode apply, WorldOpcode remove, MovementFlags flag)
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Contains(session.Sent, p => p.Opcode == apply);
        Assert.False(player.Movement.HasFlag(flag)); // the ack decides
        Assert.True(MovementControl.Acknowledge(player, MovementTypeOf(flag), 0, apply: true));
        MovementControl.ApplyReal(player, MovementTypeOf(flag), true);

        kit.System.RemoveAuras(player, spell);

        Assert.Contains(session.Sent, p => p.Opcode == remove);
        Assert.True(player.Movement.HasFlag(flag)); // still set until the client acknowledges the removal
    }

    private static MovementChangeType MovementTypeOf(MovementFlags flag) => flag switch
    {
        MovementFlags.WaterWalking => MovementChangeType.WaterWalk,
        MovementFlags.SafeFall => MovementChangeType.FeatherFall,
        _ => MovementChangeType.Hover,
    };

    [Fact]
    public void Levitate_OrdersAllThree()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Levitate, SpellCastTargets.ForSelf(), triggered: true);

        WorldOpcode[] sent = [.. session.Sent.Select(p => p.Opcode).Where(o => o is WorldOpcode.SmsgMoveFeatherFall or WorldOpcode.SmsgMoveWaterWalk or WorldOpcode.SmsgMoveSetHover)];
        Assert.Equal([WorldOpcode.SmsgMoveFeatherFall, WorldOpcode.SmsgMoveWaterWalk, WorldOpcode.SmsgMoveSetHover], sent);
    }

    [Fact]
    public void SafeFall_AmountsAreInTheLedger_AndAddUp()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, SafeFallSpell, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(17, player.Locomotion.Auras.Total(AuraType.SafeFall));
        kit.System.CastSpell(player, FelineGrace, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(34, player.Locomotion.Auras.Total(AuraType.SafeFall)); // vmangos GetTotalAuraModifier sums them

        kit.System.RemoveAuras(player, SafeFallSpell);
        Assert.Equal(17, player.Locomotion.Auras.Total(AuraType.SafeFall));
        kit.System.RemoveAuras(player, FelineGrace);
        Assert.False(player.Locomotion.Auras.Has(AuraType.SafeFall));
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgMoveFeatherFall or WorldOpcode.SmsgMoveNormalFall); // a value aura sends no order
    }

    [Fact]
    public void TheLedger_FollowsTheAuras()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, HoverSpell, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, SlowFall, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(player.Locomotion.Auras.Has(AuraType.Hover));
        Assert.True(player.Locomotion.Auras.Has(AuraType.FeatherFall));
        Assert.False(player.Locomotion.Auras.Has(AuraType.WaterWalk));

        kit.System.RemoveAuras(player, HoverSpell);
        Assert.False(player.Locomotion.Auras.Has(AuraType.Hover));
        Assert.True(player.Locomotion.Auras.Has(AuraType.FeatherFall));
    }

    [Fact]
    public void AnAuraRestoredBeforeTheClientIsInTheWorld_SetsTheFlagWithoutAPacket()
    {
        using var kit = Kit();
        var session = new FakeSession(2);
        Player player = TestWorld.CreatePlayer(2, 0, 0, session); // not in a map yet

        kit.System.CastSpell(player, WaterWalking, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(player.Movement.HasFlag(MovementFlags.WaterWalking));
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveWaterWalk);
    }
}

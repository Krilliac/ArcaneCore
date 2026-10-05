using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

public sealed class FoodDrinkVisualTests
{
    [Fact]
    public void HeartbeatWaitsForThePerUnitTimer_ThenSendsBothEffectVisuals()
    {
        using var kit = new SpellTestKit(VisualSpell(993001,
            Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen),
            Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModPowerRegen, misc: (int)PowerType.Mana)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.World.RunTick(2_000);
        player.SetStandState(StandState.Sit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 993001, SpellCastTargets.ForSelf(), true));
        Assert.Empty(Packets(session, WorldOpcode.SmsgPlaySpellVisual));
        kit.World.RunTick(3_199);
        Assert.Empty(Packets(session, WorldOpcode.SmsgPlaySpellVisual));
        kit.World.RunTick(1);
        Assert.Equal(4, Packets(session, WorldOpcode.SmsgPlaySpellVisual).Count);
        Assert.Contains(Packets(session, WorldOpcode.SmsgPlaySpellVisual), p => BitConverter.ToUInt32(p, 8) == 406);
        Assert.Contains(Packets(session, WorldOpcode.SmsgPlaySpellVisual), p => BitConverter.ToUInt32(p, 8) == 438);
        Assert.All(Packets(session, WorldOpcode.SmsgPlaySpellVisual), p => Assert.Equal(player.Guid.Value, BitConverter.ToUInt64(p, 0)));
    }

    [Fact]
    public void HeartbeatCatchupRunsOncePerElapsedInterval_AndRemovalDoesNotVisualize()
    {
        using var kit = new SpellTestKit(VisualSpell(993002, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.SetStandState(StandState.Sit);
        kit.System.CastSpell(player, 993002, SpellCastTargets.ForSelf(), true);
        session.Clear();
        kit.World.RunTick(10_400);
        Assert.Equal(2, Packets(session, WorldOpcode.SmsgPlaySpellVisual).Count);
        kit.System.RemoveAuras(player, 993002);
        session.Clear();
        kit.World.RunTick(5_200);
        Assert.Empty(Packets(session, WorldOpcode.SmsgPlaySpellVisual));
    }

    [Fact]
    public void NongenericOrNonStandingAuraDoesNotLeakHeartbeatVisuals()
    {
        using var kit = new SpellTestKit(
            VisualSpell(993003, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)),
            VisualSpell(993004, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)) with { SpellFamilyName = 1 },
            Spell(993006, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.CastSpell(player, 993003, SpellCastTargets.ForSelf(), true);
        kit.System.RemoveAuras(player, 993003);
        kit.System.CastSpell(player, 993004, SpellCastTargets.ForSelf(), true);
        kit.System.CastSpell(player, 993006, SpellCastTargets.ForSelf(), true);
        session.Clear();
        kit.World.RunTick(5_200);
        Assert.Empty(Packets(session, WorldOpcode.SmsgPlaySpellVisual));
    }

    [Fact]
    public void HeartbeatVisualsReachNearbyObserversWithTheTargetGuidOnly()
    {
        using var kit = new SpellTestKit(VisualSpell(993005, Effect(SpellEffectName.ApplyAura, 20, aura: AuraType.ModRegen)));
        (Player target, FakeSession targetSession) = kit.AddPlayer(1, 0, 0);
        (Player near, FakeSession nearSession) = kit.AddPlayer(2, 3, 0);
        (Player far, FakeSession farSession) = kit.AddPlayer(3, 500, 0);
        target.SetStandState(StandState.Sit);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(target, 993005, SpellCastTargets.ForSelf(), true));
        targetSession.Clear(); nearSession.Clear(); farSession.Clear();

        kit.World.RunTick(5_200);

        byte[] targetVisual = Assert.Single(Packets(targetSession, WorldOpcode.SmsgPlaySpellVisual));
        byte[] nearVisual = Assert.Single(Packets(nearSession, WorldOpcode.SmsgPlaySpellVisual));
        Assert.Equal(target.Guid.Value, BitConverter.ToUInt64(targetVisual, 0));
        Assert.Equal(target.Guid.Value, BitConverter.ToUInt64(nearVisual, 0));
        Assert.Equal(406u, BitConverter.ToUInt32(nearVisual, 8));
        Assert.Empty(Packets(farSession, WorldOpcode.SmsgPlaySpellVisual));
    }

    private static SpellInfo VisualSpell(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels };

    private static List<byte[]> Packets(FakeSession session, WorldOpcode opcode)
        => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];
}

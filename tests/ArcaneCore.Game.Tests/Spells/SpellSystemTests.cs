using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>The cast pipeline, cooldowns, effects and auras of <see cref="SpellSystem"/>, tick by tick.</summary>
public sealed class SpellSystemTests
{
    [Fact]
    public void InstantSpell_StartsCastsAndHeals_InVmangosPacketOrder()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, InstantHeal);
        player.Health = 30;

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, InstantHeal, SpellCastTargets.ForSelf()));

        Assert.Equal(50u, player.Health);
        Assert.Equal(
            [WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgCastResult, WorldOpcode.SmsgSpellGo, WorldOpcode.SmsgSpellheallog],
            Opcodes(session));
        Assert.Equal(new byte[] { 100, 0, 0, 0, 0 }, Packets(session, WorldOpcode.SmsgCastResult)[0]);
    }

    [Fact]
    public void UnknownAndPassiveSpells_AreIgnored()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, Passive);

        Assert.Equal(SpellCastResult.NotKnown, kit.System.HandleCastRequest(player, InstantHeal, SpellCastTargets.ForSelf()));
        Assert.Equal(SpellCastResult.NotKnown, kit.System.HandleCastRequest(player, Passive, SpellCastTargets.ForSelf()));
        Assert.Equal(SpellCastResult.NotFound, kit.System.HandleCastRequest(player, 99999, SpellCastTargets.ForSelf()));
        Assert.True(session.Sent.IsEmpty);
    }

    [Fact]
    public void GlobalCooldown_BlocksTheNextCast_UntilItExpires()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, InstantHeal, RageEnergize);

        kit.System.HandleCastRequest(player, InstantHeal, SpellCastTargets.ForSelf());
        session.Clear();
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(player, RageEnergize, SpellCastTargets.ForSelf()));
        Assert.Equal(new byte[] { 105, 0, 0, 0, 2, (byte)SpellCastResult.NotReady }, Packets(session, WorldOpcode.SmsgCastResult)[0]);

        kit.Advance(1500);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, RageEnergize, SpellCastTargets.ForSelf()));
    }

    [Fact]
    public void SpellCooldown_BlocksRecasting_ForRecoveryTime()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, CooldownSpell);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, CooldownSpell, SpellCastTargets.ForSelf()));
        kit.Advance(2000);
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(player, CooldownSpell, SpellCastTargets.ForSelf()));
        Assert.Single(kit.System.GetActiveCooldowns(player));

        kit.Advance(8000);
        Assert.Empty(kit.System.GetActiveCooldowns(player));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, CooldownSpell, SpellCastTargets.ForSelf()));
    }

    [Fact]
    public void TriggeredCast_IsInstant_SendsSpellCooldown_AndNoStartOrResult()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal([WorldOpcode.SmsgSpellCooldown, WorldOpcode.SmsgSpellGo], Opcodes(session));
        byte[] cooldown = Packets(session, WorldOpcode.SmsgSpellCooldown)[0];
        Assert.Equal(CooldownSpell, BinaryPrimitives.ReadUInt32LittleEndian(cooldown.AsSpan(8)));
        Assert.Equal(10_000u, BinaryPrimitives.ReadUInt32LittleEndian(cooldown.AsSpan(12)));
        Assert.False(kit.System.IsSpellReady(player, kit.Store.Get(CooldownSpell)!));
    }

    [Fact]
    public void CastTimeSpell_LandsAfterTheCastTime_TakesPower_AndDamagesTheTarget()
    {
        using var kit = new SpellTestKit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, 10, 0);
        kit.World.RunTick(0);
        casterSession.Clear();
        targetSession.Clear();
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(SpellCastState.Preparing, kit.System.GetState(caster.Guid)!.CurrentCast!.State);
        Assert.Equal(60u, target.Health);
        Assert.Single(Packets(targetSession, WorldOpcode.SmsgSpellStart)); // the set sees the cast start

        kit.Advance(1900);
        Assert.Equal(60u, target.Health);
        kit.Advance(100);

        Assert.Equal(45u, target.Health);
        Assert.Equal(50u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Single(Packets(targetSession, WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellGo));
    }

    [Theory]
    [InlineData(100f, 100u, SpellCastResult.OutOfRange)]
    [InlineData(10f, 10u, SpellCastResult.NoPower)]
    public void CastChecks_RangeAndPower(float distance, uint rage, SpellCastResult expected)
    {
        using var kit = new SpellTestKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, distance, 0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, rage);
        session.Clear();

        Assert.Equal(expected, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal((byte)expected, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
    }

    [Fact]
    public void HarmfulSpell_WithoutATarget_IsBadTargets_AndADeadTargetIsTargetsDead()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(new ObjectGuid(0x999))));
        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(caster.Guid)));
        target.Health = 0;
        Assert.Equal(SpellCastResult.TargetsDead, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
    }

    [Fact]
    public void Moving_InterruptsACastWithTheMovementFlag_AndResetsTheGcd()
    {
        using var kit = new SpellTestKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));
        session.Clear();

        caster.Relocate(2, 0, caster.Z, 0, kit.Now);
        kit.Advance(100);

        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Contains(WorldOpcode.SmsgSpellFailedOther, Opcodes(session));
        Assert.Equal((byte)SpellCastResult.Interrupted, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
        Assert.Equal(100u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.True(kit.System.IsSpellReady(caster, kit.Store.Get(CastBolt)!)); // GCD reset on cancel
        Assert.Equal(60u, target.Health);
    }

    [Fact]
    public void CancelCast_StopsTheMatchingPreparingCast_AndASecondCastIsInProgress()
    {
        using var kit = new SpellTestKit();
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, CastBolt, InstantHeal);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid));

        kit.Advance(1500); // past the GCD, still casting
        Assert.Equal(SpellCastResult.SpellInProgress, kit.System.HandleCastRequest(caster, InstantHeal, SpellCastTargets.ForSelf()));
        kit.System.CancelCast(caster, InstantHeal); // not the cast in progress
        Assert.NotNull(kit.System.GetState(caster.Guid)!.CurrentCast);

        session.Clear();
        kit.System.CancelCast(caster, CastBolt);
        Assert.Null(kit.System.GetState(caster.Guid)?.CurrentCast);
        Assert.Equal((byte)SpellCastResult.Interrupted, Packets(session, WorldOpcode.SmsgCastResult).Single()[5]);
        kit.Advance(2000);
        Assert.Equal(60u, target.Health);
    }

    [Fact]
    public void PeriodicDamageAura_TakesANegativeSlot_TicksAndExpires()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, FakeSession targetSession) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, DotSpell);
        targetSession.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, DotSpell, SpellCastTargets.ForUnit(target.Guid)));

        SpellAuraHolder holder = kit.System.GetAuras(target).Single();
        Assert.Equal(32, holder.Slot);
        Assert.False(holder.IsPositive);
        Assert.Equal(DotSpell, target.GetUInt32(UpdateFields.UnitFieldAura + 32));
        Assert.Equal(1u, target.GetUInt32(UpdateFields.UnitFieldAuralevels + 8) & 0xFF); // caster level 1
        Assert.Equal(new byte[] { 32, 0xE0, 0x2E, 0, 0 }, Packets(targetSession, WorldOpcode.SmsgUpdateAuraDuration).Single());

        kit.Advance(12_000);

        Assert.Equal(60u - (4 * 4), target.Health);
        Assert.Equal(4, Packets(targetSession, WorldOpcode.SmsgPeriodicauralog).Count);
        Assert.Empty(kit.System.GetAuras(target));
        Assert.Equal(0u, target.GetUInt32(UpdateFields.UnitFieldAura + 32));
    }

    [Fact]
    public void PeriodicHeal_IsPositive_UsesSlotZero_AndCanBeCancelled()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, HotSpell);
        player.Health = 10;

        kit.System.HandleCastRequest(player, HotSpell, SpellCastTargets.ForSelf());
        SpellAuraHolder holder = kit.System.GetAuras(player).Single();
        Assert.Equal(0, holder.Slot);
        Assert.Equal(HotSpell, player.GetUInt32(UpdateFields.UnitFieldAura));
        Assert.Equal(0x9u, player.GetUInt32(UpdateFields.UnitFieldAuraflags) & 0xF); // cancelable + effect 0 (vmangos)

        kit.Advance(2000);
        Assert.Equal(20u, player.Health);

        kit.System.CancelAura(player, HotSpell);
        Assert.Empty(kit.System.GetAuras(player));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldAura));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldAuraflags));
    }

    [Fact]
    public void CancelAura_IgnoresHarmfulAuras()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.System.CastSpell(caster, DotSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        kit.System.CancelAura(target, DotSpell);

        Assert.Single(kit.System.GetAuras(target));
    }

    [Fact]
    public void PassiveSpell_IsPermanent_AndHasNoVisibleSlot()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Passive, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(60_000, 1000);

        SpellAuraHolder holder = kit.System.GetAuras(player).Single();
        Assert.True(holder.IsPermanent);
        Assert.Equal(SpellAuraHolder.NoSlot, holder.Slot);
        Assert.Empty(Packets(session, WorldOpcode.SmsgUpdateAuraDuration));
    }

    [Fact]
    public void Stun_SetsTheUnitFlag_UntilTheAuraExpires()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);

        kit.System.CastSpell(caster, StunSpell, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.True((target.UnitFlags & UnitFlags.Stunned) != 0);

        kit.Advance(4000);
        Assert.True((target.UnitFlags & UnitFlags.Stunned) == 0);
    }

    [Fact]
    public void Energize_AddsPower_AndLogsIt()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, RageEnergize, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(100u, SpellSystem.GetPower(player, PowerType.Rage));
        Assert.Single(Packets(session, WorldOpcode.SmsgSpellenergizelog));
    }

    [Fact]
    public void LearnSpellEffect_TeachesTheTriggerSpell_AndSendsLearnedSpell()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Teacher, SpellCastTargets.ForSelf(), triggered: true);

        Assert.True(kit.Spellbook.HasSpell(player, CooldownSpell));
        Assert.Equal(BitConverter.GetBytes(CooldownSpell), Packets(session, WorldOpcode.SmsgLearnedSpell).Single());
        Assert.False(kit.System.LearnSpell(player, CooldownSpell)); // already known
    }

    [Fact]
    public void TeleportUnits_ToTheHomeBindAndToADatabasePosition()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Home = new HomeBind(0, 12, 10, 20, 83.5f);

        kit.System.CastSpell(player, HomeTeleport, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal((10f, 20f), (player.X, player.Y));
        Assert.Single(Packets(session, WorldOpcode.MsgMoveTeleportAck));

        kit.System.CastSpell(player, DatabaseTeleport, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal((40f, 50f, 1.5f), (player.X, player.Y, player.Orientation));
    }

    [Fact]
    public void Channel_SetsTheChannelFields_AndCancellingRemovesItsAura()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);

        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());
        Assert.Equal(ChannelSpell, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Single(Packets(session, WorldOpcode.MsgChannelStart));
        Assert.True(kit.System.HasAura(player, ChannelSpell));
        session.Clear();

        kit.System.CancelChannel(player);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.False(kit.System.HasAura(player, ChannelSpell));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Packets(session, WorldOpcode.MsgChannelUpdate).Single());
    }

    [Fact]
    public void Channel_EndsByItself_AfterItsDuration()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, ChannelSpell);
        kit.System.HandleCastRequest(player, ChannelSpell, SpellCastTargets.ForSelf());

        kit.Advance(5000);

        Assert.Null(kit.System.GetState(player.Guid)?.CurrentCast);
        kit.Advance(1000); // a normal end clears the channel values 1000 ms later (vmangos ChannelResetEvent)
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitChannelSpell));
    }

    [Fact]
    public void RemoveUnit_DropsCastsAurasAndCooldowns()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Passive, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true);

        kit.System.RemoveUnit(player);

        Assert.Null(kit.System.GetState(player.Guid));
        Assert.Equal(0, kit.System.TrackedUnitCount);
    }

    [Fact]
    public void ClearCooldown_MakesTheSpellReady_AndTellsTheClient()
    {
        using var kit = new SpellTestKit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true);
        session.Clear();

        kit.System.ClearCooldown(player, CooldownSpell);

        Assert.True(kit.System.IsSpellReady(player, kit.Store.Get(CooldownSpell)!));
        Assert.Single(Packets(session, WorldOpcode.SmsgClearCooldown));
    }

    [Fact]
    public void Store_CreateSpellsAreExactRaceClass_AndTargetPositionsResolve()
    {
        using var kit = new SpellTestKit();
        Assert.Equal(new[] { Passive, InstantHeal }, kit.Store.GetCreateSpells(1, 1));
        Assert.Empty(kit.Store.GetCreateSpells(2, 1));
        Assert.Equal(new SpellTargetPosition(0, 40, 50, 83.5f, 1.5f), kit.Store.GetTargetPosition(DatabaseTeleport));
        Assert.Null(kit.Store.GetTargetPosition(HomeTeleport));
    }
}

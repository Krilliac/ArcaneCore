using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>SPELL_AURA_MOUNTED (vmangos HandleAuraMounted, SpellAuras.cpp:2251-2276; Unit::Mount/Unmount, Unit.cpp:5794-5842; Player::Mount, Player.cpp:18170-18260).</summary>
public sealed class MountAuraTests
{
    private const uint BrownHorse = 960001;      // aura 78 (entry 2402) + aura 32 +100, as a classic riding mount lists them
    private const uint GreyWolf = 960002;        // aura 78 (entry 3000) + aura 32 +60
    private const uint Stealthy = 960003;        // an aura that ends on mounting
    private const uint WaterBound = 960004;      // an aura that ends on dismounting
    private const uint Ghostly = 960005;         // aura 78 for a creature that does not exist
    private const uint WarhorseSkin = 960006;    // aura 32 alone (a speed aura of a mount that is not mounted)

    private sealed class Displays : IMountDisplaySource
    {
        public Dictionary<uint, uint> ByEntry { get; } = new() { [2402] = 14337, [3000] = 207 };

        public uint? FindMountDisplay(uint creatureEntry) => ByEntry.TryGetValue(creatureEntry, out uint display) ? display : null;
    }

    private static SpellTestKit Kit()
    {
        SpellInfo Perm(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with
        {
            Duration = new SpellDuration(-1, 0, -1),
            SpellVisual = 1,
        };

        var kit = new SpellTestKit(
            Perm(BrownHorse, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 2402), Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModIncreaseMountedSpeed)),
            Perm(GreyWolf, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 3000), Effect(SpellEffectName.ApplyAura, 60, aura: AuraType.ModIncreaseMountedSpeed)),
            Perm(Stealthy, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { AuraInterruptFlags = SpellAuraInterruptFlags.MountCancels },
            Perm(WaterBound, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with { AuraInterruptFlags = SpellAuraInterruptFlags.DismountCancels },
            Perm(Ghostly, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 9999)),
            Perm(WarhorseSkin, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModIncreaseMountedSpeed)));
        LocomotionEnvironment.RegisterMountDisplays(kit.World, new Displays());
        return kit;
    }

    private static byte[][] Of(FakeSession session, WorldOpcode opcode) => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    [Fact]
    public void TheModuleIsDiscovered()
    {
        using var kit = Kit();
        Assert.Contains(typeof(MountAura), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.Mounted));
    }

    [Fact]
    public void Mounting_SetsTheDisplay_SaysOk_AndTheMountedSpeedAppliesAsTheAckArrives()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        Assert.True(player.Combat.QueueExtraAttacks(2));

        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal(0u, player.Combat.ExtraAttacks);
        Assert.Equal(MountService.ResultPacket(10), Assert.Single(Of(session, WorldOpcode.SmsgMountresult))); // MOUNTRESULT_OK = 10
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 0, 14.0f), Assert.Single(Of(session, WorldOpcode.SmsgForceRunSpeedChange))); // 7 x (1 + 100%)
    }

    [Fact]
    public void Dismounting_ClearsTheDisplay_SaysOk_AndTheSpeedGoesBack()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);
        MovementControl.AcknowledgeSpeed(player, MoveType.Run, 0, 14.0f);
        UnitSpeed.SetReal(player, MoveType.Run, 14.0f);
        session.Clear();

        kit.System.RemoveAuras(player, BrownHorse);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal(MountService.ResultPacket(3), Assert.Single(Of(session, WorldOpcode.SmsgDismountresult))); // DISMOUNTRESULT_OK = 3
        Assert.Equal(SpeedPackets.BuildForceChange(player.Guid.Value, 1, 7.0f), Assert.Single(Of(session, WorldOpcode.SmsgForceRunSpeedChange)));
    }

    [Fact]
    public void ACreatureThatDoesNotExist_LeavesThePlayerOnFoot_WithoutAResult()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, Ghostly, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.DoesNotContain(session.Sent, p => p.Opcode is WorldOpcode.SmsgMountresult or WorldOpcode.SmsgDismountresult);

        kit.System.RemoveAuras(player, Ghostly); // removing it dismounts nothing and says nothing
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDismountresult);
    }

    [Fact]
    public void WithoutAMountDisplaySource_NoOneMounts()
    {
        using var kit = new SpellTestKit(Spell(960010, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: 2402)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });
        (Player player, _) = kit.AddPlayer(1);

        kit.System.CastSpell(player, 960010, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void Mounting_RemovesTheAurasThatEndOnMounting_AndDismountingTheOnesThatEndOnDismount()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        kit.System.CastSpell(player, Stealthy, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(player, WaterBound, SpellCastTargets.ForSelf(), triggered: true);
        Assert.True(kit.System.HasAura(player, Stealthy));

        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);

        Assert.False(kit.System.HasAura(player, Stealthy));  // AURA_INTERRUPT_MOUNT_CANCELS
        Assert.True(kit.System.HasAura(player, WaterBound)); // not affected by mounting

        kit.System.RemoveAuras(player, BrownHorse);

        Assert.False(kit.System.HasAura(player, WaterBound)); // AURA_INTERRUPT_DISMOUNT_CANCELS
    }

    [Fact]
    public void AMountedPlayer_IsToldItIsAlreadyMounted_AndKeepsItsMount()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);
        session.Clear();

        kit.System.CastSpell(player, GreyWolf, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(MountService.ResultPacket(2), Assert.Single(Of(session, WorldOpcode.SmsgMountresult))); // MOUNTRESULT_ALREADYMOUNTED
        Assert.Equal(14337u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
    }

    [Fact]
    public void ALootingPlayer_CannotMount_AndTheMountAuraIsRemoved()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.UnitFlags |= UnitFlags.Looting;
        Assert.True(player.Combat.QueueExtraAttacks(2));

        kit.System.CastSpell(player, BrownHorse, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(MountService.ResultPacket(6), Assert.Single(Of(session, WorldOpcode.SmsgMountresult))); // MOUNTRESULT_LOOTING
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldMountdisplayid));
        Assert.Equal(2u, player.Combat.ExtraAttacks);
        Assert.False(kit.System.HasAura(player, BrownHorse));
    }

    [Fact]
    public void ASpeedAuraOfAMount_DoesNothingWhileOnFoot()
    {
        using var kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1);

        kit.System.CastSpell(player, WarhorseSkin, SpellCastTargets.ForSelf(), triggered: true);

        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgForceRunSpeedChange);
    }

    [Fact]
    public void TheResultPackets_AreAU32()
    {
        Assert.Equal(new byte[] { 10, 0, 0, 0 }, MountService.ResultPacket(MountResults.Ok));
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, MountService.ResultPacket(MountResults.DismountOk));
    }
}

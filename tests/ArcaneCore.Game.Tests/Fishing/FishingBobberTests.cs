using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Fishing;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Fishing;

/// <summary>
/// The cast half of fishing: target 39 + TRANS_DOOR (vmangos Spell::EffectTransmitted, SpellEffects.cpp:5648-5790) summons an owned bobber on water,
/// refuses land (NOT_FISHABLE), makes the bobber the channel object, bites <c>lastSec</c> in {3,7,13,17} before the channel ends, gives five seconds to
/// click and otherwise ends the channel with SMSG_FISH_NOT_HOOKED (GameObject.cpp:359-379, 411-424).
/// </summary>
public sealed class FishingBobberTests
{
    private static List<byte[]> Sent(FishingRig rig, WorldOpcode opcode) => Packets(rig.Session, opcode);

    [Fact]
    public void CastOnLand_AnswersNotFishable_SpawnsNothing_AndEndsTheChannel()
    {
        using var rig = new FishingRig();
        rig.Terrain.Water = false;

        rig.Cast();

        Assert.Null(rig.Bobber);
        byte[] last = Sent(rig, WorldOpcode.SmsgCastResult).Last();
        Assert.Equal((FishingRig.FishingSpell, (byte)SpellCastResultStatus.Failure, (byte)SpellCastResult.NotFishable), (BitConverter.ToUInt32(last, 0), last[4], last[5]));
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(0ul, rig.Player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Equal(0, rig.Fishing.ActiveBobbers);
    }

    [Fact]
    public void WaterThatIsNotDeepEnough_StillTakesTheBobber_LikeVmangos_ButAGroundNearZeroDoesNot()
    {
        // vmangos ignores IsSwimmable's answer (SpellEffects.cpp:5712-5724): it only probes a second time from the surface level, then refuses
        // when |depth_level| < 1 (the ground height under the liquid) or the water is out of sight. Ported literally.
        using var shallow = new FishingRig();
        shallow.Terrain.Depth = 79.5f; // 0.5 yd of water: IsSwimmable is false twice, the guard passes
        shallow.Cast();
        Assert.Equal(2, shallow.Terrain.Calls);
        Assert.NotNull(shallow.Bobber);

        using var nearZero = new FishingRig();
        nearZero.Terrain.Depth = 0.5f;
        nearZero.Cast();
        Assert.Null(nearZero.Bobber);
        Assert.Equal(SpellCastResult.NotFishable, (SpellCastResult)Sent(nearZero, WorldOpcode.SmsgCastResult).Last()[5]);
    }

    [Fact]
    public void CastOnWater_SummonsAnOwnedBobber_AtTheWaterLevel_AndMakesItTheChannelObject()
    {
        using var rig = new FishingRig();

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());

        GameObject bobber = rig.Bobber!;
        Assert.Equal(FishingRig.BobberEntry, bobber.Entry);
        Assert.Equal(rig.Player.Guid, bobber.OwnerGuid);
        Assert.Equal(rig.Player.Guid.Value, bobber.GetUInt64(UpdateFields.ObjectFieldCreatedBy));
        Assert.Equal(FishingRig.FishingSpell, bobber.SpellId);
        Assert.Equal(rig.Player.Level, bobber.GetUInt32(UpdateFields.GameobjectLevel));
        Assert.Equal(GameObjectLootState.NotReady, bobber.LootState);
        Assert.Equal(rig.Terrain.Level, bobber.Z);
        Assert.Equal(15.0f, bobber.X - rig.Player.X, 3); // the effect radius straight ahead (orientation 0)
        Assert.Equal(bobber.Guid.Value, rig.Player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Equal(FishingRig.FishingSpell, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Single(Sent(rig, WorldOpcode.MsgChannelStart));
    }

    [Fact]
    public void Position_IsTheDestination_WhenTheCastNamesOne_AndARandomPointInTheRangeWithoutARadius()
    {
        using var rig = new FishingRig();
        rig.Kit.System.CastSpell(rig.Player, FishingRig.FishingSpell,
            new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (7.0f, 3.0f, 83.5f) }, triggered: false);
        Assert.Equal((7.0f, 3.0f), (rig.Bobber!.X, rig.Bobber.Y));

        using var ranged = new FishingRig(effect: SpellTestKit.Effect(SpellEffectName.TransDoor, 0, SpellImplicitTarget.LocationCasterFishingSpot, misc: (int)FishingRig.BobberEntry));
        // PickRandom.NextSingle() is 0: distance = min range, no angle offset (the Spell helper has range (0, 0): the bobber lands on the caster).
        ranged.Cast();
        Assert.Equal((ranged.Player.X, ranged.Player.Y), (ranged.Bobber!.X, ranged.Bobber.Y));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 7)]
    [InlineData(2, 13)]
    [InlineData(3, 17)]
    public void TheBobberBites_LastSecBeforeTheChannelEnds_AndExpiresFiveSecondsLater(int index, int lastSec)
    {
        using var rig = new FishingRig(durationMs: 30000);
        rig.Pick.Index = index;
        rig.Cast();
        GameObject bobber = rig.Bobber!;
        uint readyAt = (uint)((30 - lastSec) * 1000);

        rig.Step(readyAt - 50);
        Assert.Equal(GameObjectLootState.NotReady, bobber.LootState);
        Assert.Empty(Sent(rig, WorldOpcode.SmsgGameobjectCustomAnim));

        rig.Step(50);
        Assert.Equal(GameObjectLootState.Ready, bobber.LootState);
        Assert.Equal(GameObjectState.Active, bobber.State);
        Assert.Single(Sent(rig, WorldOpcode.SmsgGameobjectCustomAnim));
        Assert.Empty(Sent(rig, WorldOpcode.SmsgFishNotHooked));
        // The click window is FISHING_BOBBER_READY_TIME (5 s); a ready bobber nobody clicked ends it.
        rig.Step(4900);
        Assert.Empty(Sent(rig, WorldOpcode.SmsgFishNotHooked));
    }

    [Fact]
    public void TheBiteBroadcastsTheSplashSound3355AndTheCustomAnimation()
    {
        using var rig = new FishingRig(durationMs: 30000);
        rig.Pick.Index = 0; // lastSec 3: the bite is at 27 s
        rig.Cast();

        rig.Step(27000);

        byte[] sound = Assert.Single(Sent(rig, WorldOpcode.SmsgPlayObjectSound));
        var reader = new PacketReader(sound);
        Assert.Equal(FishingService.SplashSoundId, reader.ReadUInt32());
        Assert.Equal(rig.Bobber!.Guid.Value, reader.ReadUInt64());
        Assert.Equal(3355u, FishingService.SplashSoundId);
        var anim = new PacketReader(Assert.Single(Sent(rig, WorldOpcode.SmsgGameobjectCustomAnim)));
        Assert.Equal((rig.Bobber.Guid.Value, 0u), (anim.ReadUInt64(), anim.ReadUInt32()));
    }

    [Fact]
    public void AnUnclickedBobber_Expires_WithFishNotHooked_TheChannelEnds_AndTheBobberGoes()
    {
        using var rig = new FishingRig(durationMs: 30000);
        rig.Pick.Index = 1; // lastSec 7: ready at 23 s, expires at 28 s (before the 30 s channel ends)
        rig.Cast();

        rig.Step(27950);
        Assert.NotNull(rig.Bobber);
        Assert.Empty(Sent(rig, WorldOpcode.SmsgFishNotHooked));

        rig.Step(50);
        Assert.Single(Sent(rig, WorldOpcode.SmsgFishNotHooked));
        Assert.Empty(Sent(rig, WorldOpcode.SmsgFishNotHooked).Single());
        Assert.Null(rig.Bobber);
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(0ul, rig.Player.GetUInt64(UpdateFields.UnitFieldChannelObject));
        Assert.Equal(0, rig.Fishing.ActiveBobbers);
    }

    [Fact]
    public void MovingDuringTheChannel_CancelsTheCast_AndRemovesTheBobber()
    {
        using var rig = new FishingRig();
        rig.Cast();
        Assert.NotNull(rig.Bobber);

        rig.Player.Relocate(rig.Player.X + 3, rig.Player.Y, rig.Player.Z, 0, 0);
        rig.Step(100);

        Assert.Null(rig.Bobber);
        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Equal(0, rig.Fishing.ActiveBobbers);
    }

    [Fact]
    public void CancellingTheChannel_RemovesTheBobber_AndACastAgainStartsAFreshOne()
    {
        using var rig = new FishingRig();
        rig.Cast();
        ObjectGuid first = rig.Bobber!.Guid;

        rig.Kit.System.CancelChannel(rig.Player);
        Assert.Null(rig.Bobber);

        rig.Cast();
        Assert.NotEqual(first, rig.Bobber!.Guid);
        Assert.Equal(1, rig.Fishing.ActiveBobbers);
    }

    [Fact]
    public void RecastingWhileChannelling_ReplacesTheOldBobber()
    {
        using var rig = new FishingRig();
        rig.Cast();
        ObjectGuid first = rig.Bobber!.Guid;

        rig.Cast();

        Assert.Single(rig.Objects.GameObjects, g => g.Type == GameObjectType.FishingNode);
        Assert.NotEqual(first, rig.Bobber!.Guid);
    }

    [Fact]
    public void FinishChannel_EndsTheChannelWithoutAnInterrupt_AndReportsWhetherThereWasOne()
    {
        using var rig = new FishingRig();
        Assert.False(rig.Kit.System.FinishChannel(rig.Player));
        rig.Cast();
        rig.Session.Clear();

        Assert.True(rig.Kit.System.FinishChannel(rig.Player));

        Assert.Equal(0u, rig.Player.GetUInt32(UpdateFields.UnitChannelSpell));
        Assert.Empty(Sent(rig, WorldOpcode.SmsgSpellFailedOther)); // not an interrupt
        Assert.Single(Sent(rig, WorldOpcode.MsgChannelUpdate));
        Assert.Null(rig.Bobber);
    }

    [Theory]
    [InlineData(LiquidStatus.InWater, 80f, -30f, 83.5f, true)]
    [InlineData(LiquidStatus.UnderWater, 80f, -30f, 70f, true)]
    [InlineData(LiquidStatus.WaterWalk, 80f, -30f, 80f, true)]
    [InlineData(LiquidStatus.AboveWater, 80f, -30f, 80.5f, true)]   // within JUMP_HEIGHT 0.6 of the surface
    [InlineData(LiquidStatus.AboveWater, 80f, -30f, 81f, false)]    // too high above the water
    [InlineData(LiquidStatus.NoWater, 80f, -30f, 80f, false)]
    [InlineData(LiquidStatus.InWater, 80f, 79f, 80f, false)]        // not deeper than 1.5 yd
    [InlineData(LiquidStatus.InWater, 80f, 78.5f, 80f, false)]      // exactly 1.5 deep: strictly greater is required
    [InlineData(LiquidStatus.InWater, 80f, 78.4f, 80f, true)]
    public void MapFishingTerrain_IsAPortOfIsSwimmable(LiquidStatus status, float level, float depth, float z, bool expected)
        => Assert.Equal(expected, MapFishingTerrain.Evaluate(status, new LiquidData(1, LiquidTypeFlags.Water, level, depth), z, 1.5f));
}
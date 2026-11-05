using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// SMSG_GAMEOBJECT_RESET_STATE when an object whose lock has SLOW_OPEN in its second slot (the capture banners) respawns
/// (vmangos GameObject::Update GO_READY respawn, GameObject.cpp:445-453; SendGameObjectReset, GameObject.cpp:2460-2465).
/// </summary>
public sealed class GameObjectResetStateTests
{
    private static void RespawnAfterDespawn(GameObjectTypeRig rig, GameObject go, FakeSession? clearAfterDespawn = null)
    {
        Assert.True(rig.System.DespawnForRespawn(go));
        rig.World.RunTick(10);
        rig.World.RunTick(10);
        Assert.False(go.IsSpawned);
        clearAfterDespawn?.Clear(); // the out-of-range entry of the despawn is not the create under test
        rig.System.SetRespawnIn(go, 1);
        rig.World.RunTick(1100);
        Assert.True(go.IsSpawned);
    }

    [Fact]
    public void ASlowOpenLockedObject_SendsTheResetStateToAPlayerInRangeWhenItRespawns()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, Banner, 3, 0)]);
        (_, var session) = rig.Join(1, 1, 0);
        GameObject banner = rig.Single(Banner);
        session.Clear();

        RespawnAfterDespawn(rig, banner);

        (WorldOpcode _, byte[] payload) = Assert.Single(Packets(session, WorldOpcode.SmsgGameobjectResetState));
        Assert.Equal(GameObjectPackets.ResetState(banner.Guid), payload);
    }

    [Fact]
    public void ASlowOpenLockedObject_SendsItsCreateToAPlayerInRangeBeforeTheResetState()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, Banner, 3, 0)]);
        (_, var session) = rig.Join(1, 1, 0);
        GameObject banner = rig.Single(Banner);
        session.Clear();

        RespawnAfterDespawn(rig, banner, session);

        var sent = session.Sent.ToArray();
        int reset = Array.FindIndex(sent, p => p.Opcode == WorldOpcode.SmsgGameobjectResetState);
        int create = Array.FindIndex(sent, p => p.Opcode is WorldOpcode.SmsgUpdateObject or WorldOpcode.SmsgCompressedUpdateObject);
        Assert.True(create >= 0, "the player never received the create block");
        Assert.True(reset >= 0, "the player never received the reset state");
        Assert.True(create < reset, "the reset state went out before the create block");
        Assert.Single(sent, p => p.Opcode == WorldOpcode.SmsgGameobjectResetState);
    }

    [Fact]
    public void AnObjectWithoutASlowOpenLock_SendsNoResetStateWhenItRespawns()
    {
        GameObjectTypeRig rig = Create([GoSpawn(1, EveryoneGoober, 3, 0)]);
        (_, var session) = rig.Join(1, 1, 0);
        GameObject goober = rig.Single(EveryoneGoober);
        session.Clear();

        RespawnAfterDespawn(rig, goober);

        Assert.Empty(Packets(session, WorldOpcode.SmsgGameobjectResetState));
    }
}

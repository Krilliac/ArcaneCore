using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.Collision;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// A goober use rewards the user's group at the object (vmangos GameObject::Use, GAMEOBJECT_TYPE_GOOBER, GameObject.cpp:1590 calls
/// Player::RewardPlayerAndGroupAtCast, Player.cpp:20019-20040): every member within the group reward distance and alive or not yet
/// released gets CastedCreatureOrGO, and only the user is the original caster, so the others progress sharable quests alone. A button
/// of display 295 (the Scarlet Monastery levers) is refused to a user without line of sight (GameObject.cpp:1441-1446).
/// </summary>
public sealed class GooberGroupCreditTests
{
    [Fact]
    public void GooberUse_CreditsTheGroupMembersInRange_AndOnlyTheUserIsTheOriginalCaster()
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GoSpawn(1, EventGoober, 3, 0)]);
        (Player user, _) = rig.Join(1);
        (Player near, _) = rig.Join(2, 4, 0);
        (Player far, _) = rig.Join(3, 900, 900);
        var groups = new FakeGroups();
        groups.Create(ArcaneCore.Game.Groups.LootMethod.FreeForAll, user, near, far);
        rig.System.Loot!.Groups = groups;

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(user, rig.Single(EventGoober).Guid));

        Assert.Equal(
            [(user.Guid, EventGoober, true), (near.Guid, EventGoober, false)],
            rig.Quests.Credits.OrderBy(c => c.Player == user.Guid ? 0 : 1).ToList());
    }

    [Fact]
    public void GooberUse_SkipsAReleasedGhost_ButCreditsADeadMemberWhoseBodyIsNotReleased()
    {
        // Player::RewardPlayerAndGroupAtCast (Player.cpp:20019-20040) credits a member that IsAlive() or is not PLAYER_FLAGS_GHOST.
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GoSpawn(1, EventGoober, 3, 0)]);
        (Player user, _) = rig.Join(1);
        (Player corpse, _) = rig.Join(2, 4, 0);
        (Player ghost, _) = rig.Join(3, 5, 0);
        var groups = new FakeGroups();
        groups.Create(ArcaneCore.Game.Groups.LootMethod.FreeForAll, user, corpse, ghost);
        rig.System.Loot!.Groups = groups;
        corpse.Combat.DeathState = ArcaneCore.Game.Combat.DeathState.Dead;
        ghost.Combat.DeathState = ArcaneCore.Game.Combat.DeathState.Dead;
        ghost.Flags |= PlayerFlags.Ghost;

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(user, rig.Single(EventGoober).Guid));

        Assert.Equal(
            [(user.Guid, EventGoober, true), (corpse.Guid, EventGoober, false)],
            rig.Quests.Credits.OrderBy(c => c.Player == user.Guid ? 0 : 1).ToList());
    }

    [Fact]
    public void GooberUse_ByAPlayerWithoutAGroup_CreditsOnlyThePlayer()
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GoSpawn(1, EventGoober, 3, 0)]);
        (Player user, _) = rig.Join(1);
        rig.Join(2, 4, 0);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(user, rig.Single(EventGoober).Guid));

        Assert.Equal([(user.Guid, EventGoober, true)], rig.Quests.Credits);
    }

    [Fact]
    public void ADisplay295Button_BehindAWall_IsRefused_AndOpensWithLineOfSight()
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create([GoSpawn(1, Lever295, 10, 0)]);
        WorldCollision.Of(rig.World).Install(new CollisionSeamTests.WallAtX(11));
        (Player behind, _) = rig.Join(1, 12, 0);
        GameObject lever = rig.Single(Lever295);

        Assert.Equal(GameObjectUseResult.LineOfSight, rig.System.Use(behind, lever.Guid));
        Assert.Equal(GameObjectState.Ready, lever.State);

        (Player inSight, _) = rig.Join(2, 9, 0);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(inSight, lever.Guid));
        Assert.Equal(GameObjectState.Active, lever.State);
    }
}

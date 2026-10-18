using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The ends of an object's sight line as <see cref="MapCollision"/> computes them (vmangos WorldObject::IsWithinLOS, IsWithinLOSAtPosition and
/// GetLosCheckPosition, Objects/Object.cpp:1766-1781, Object.h:607-611) and the collision height a unit looks from (Unit::GetCollisionHeight,
/// Unit.h:542). <see cref="ObjectLineOfSightHeightTests"/> checks the same heights against real vmap geometry.
/// </summary>
public sealed class LineOfSightSegmentTests
{
    /// <summary>
    /// IsWithinLOS (Object.h:607-610): from the viewer's collision height to the point raised by targetHeight, 2 by default;
    /// <see cref="MapCollision.LineOfSightSegment"/> is the segment the object overload tests.
    /// </summary>
    [Fact]
    public void PointLineOfSight_RaisesThePointByTargetHeight_FromTheViewersCollisionHeight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var probe = new CollisionSeamTests.RecordingLineOfSight();
        WorldCollision.Of(world).Install(probe);
        Player a = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        Player b = TestWorld.CreatePlayer(2, 10, 0, new FakeSession(2));
        world.AddPlayer(a);
        world.AddPlayer(b);
        Map map = world.GetMap(0);
        a.Locomotion.CollisionHeight = 1.5f;
        var eye = new Vector3(a.X, a.Y, a.Z + 1.5f);

        Assert.Equal(1.5f, MapCollision.EyeHeightOf(a));
        Assert.True(map.Collision.IsWithinLineOfSight(a, 10, 0, 5));
        Assert.True(map.Collision.IsWithinLineOfSight(a, 10, 0, 5, targetHeight: 0.5f));
        Assert.True(map.Collision.IsWithinLineOfSight(a, b));
        Assert.Equal([(eye, new Vector3(10, 0, 7)), (eye, new Vector3(10, 0, 5.5f)), (eye, new Vector3(b.X, b.Y, b.Z + 1f))], probe.Calls);
        Assert.Equal(probe.Calls[^1], MapCollision.LineOfSightSegment(a, b));
    }

    /// <summary>An object that is not a unit looks from 1 yd (IsWithinLOSAtPosition, Object.cpp:1770) and is looked at 1 yd up (:1777-1781).</summary>
    [Fact]
    public void AnObjectThatIsNotAUnit_LooksFromOneYard()
    {
        var template = new GameObjectTemplate
        {
            Entry = 990_804, Type = (uint)GameObjectType.Generic, DisplayId = 1, Name = "Crate", Data = new uint[GameObjectTemplate.DataCount],
        };
        Assert.Equal(1f, MapCollision.EyeHeightOf(new GameObject(1, template, spawn: null)));
        Assert.Equal(1f, MapCollision.ObjectEyeHeight);
        Assert.Equal(1f, MapCollision.ObjectTargetHeight);
    }

    /// <summary>
    /// GetCollisionHeight is the model height times m_nativeScaleOverride: the scale Creature::InitEntry chose for a creature
    /// (Creature.cpp:403-404), 1 for a player (Unit.h:486), the transformation's scale while one is applied (SetTransformScale,
    /// Unit.cpp:10967-10976) and m_nativeScale again once it is reset (:10978-10986).
    /// </summary>
    [Fact]
    public void AUnitsCollisionHeight_IsItsModelHeightTimesItsScaleOverride()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        var template = new CreatureTemplate
        {
            Entry = 990_806, Name = "Watcher", MinLevel = 10, MaxLevel = 10, MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1],
            Faction = 35, Scale = 2f,
        };
        var big = new Creature(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));

        Assert.Equal(2f, player.CollisionHeight);
        player.Locomotion.CollisionHeight = 1.5f;
        Assert.Equal(1.5f, player.CollisionHeight);
        TransformScale.Set(player, 0.8f);
        Assert.Equal(1.2f, player.CollisionHeight, 4);
        TransformScale.Reset(player);
        Assert.Equal(1.5f, player.CollisionHeight);

        Assert.Equal(4f, big.CollisionHeight);
        TransformScale.Set(big, 0.8f);
        Assert.Equal(1.6f, big.CollisionHeight, 4);
        TransformScale.Reset(big);
        Assert.Equal(4f, big.CollisionHeight);
    }
}

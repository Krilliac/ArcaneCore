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
/// How high each end of an object's line of sight sits (vmangos WorldObject::IsWithinLOSInMap, IsWithinLOS and IsWithinLOSAtPosition,
/// Objects/Object.cpp:1754-1781, Object.h:607-611), checked against a real vmap wall (<see cref="VMapWall"/>) whose top lies between
/// the heights vmangos uses and a flat two yards at both ends. Players stand on z 83.5 at y 100; the wall rises from there.
/// </summary>
public sealed class ObjectLineOfSightHeightTests
{
    private const float Floor = 83.5f;
    private const float Y = 100f;

    private static (Map Map, Player A, Player B) TwoPlayers(WorldRuntime world, float ax = 90f, float bx = 110f)
    {
        Player a = TestWorld.CreatePlayer(1, ax, Y, new FakeSession(1));
        Player b = TestWorld.CreatePlayer(2, bx, Y, new FakeSession(2));
        world.AddPlayer(a);
        world.AddPlayer(b);
        return (world.GetMap(0), a, b);
    }

    private static Creature AddCreature(Map map, uint entry, float x, float scale)
    {
        var template = new CreatureTemplate
        {
            Entry = entry, Name = "Watcher", MinLevel = 10, MaxLevel = 10, MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1],
            Faction = 35, Scale = scale,
        };
        var creature = new Creature(entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Relocate(x, Y, Floor, 0f, 0);
        creature.MapId = 0;
        map.AddObject(creature);
        return creature;
    }

    /// <summary>
    /// The viewer looks from its collision height (2 yd without model data, Unit.cpp:117) to one yard above the target (GetLosCheckPosition,
    /// Object.cpp:1777-1781). A 1.5 yd wall 2 yd in front of the target, 20 yd from the viewer: the viewer's line has come down to 1.1 yd
    /// there and is blocked; the target's line back to the viewer is still at 1.9 yd there and passes.
    /// </summary>
    [Fact]
    public void UnitToUnit_LooksFromTheViewersCollisionHeight_ToOneYardAboveTheTarget()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (_, Player a, Player b) = TwoPlayers(world);
        using VMapWall wall = VMapWall.AcrossX(world, 108f, Y, Floor, 1.5f);

        Assert.False(a.IsWithinLineOfSight(b));
        Assert.True(b.IsWithinLineOfSight(a));
    }

    /// <summary>
    /// Unit::GetCollisionHeight (Unit.h:542) starts from the model's collision height: a unit 1 yd tall looks along a flat line 1 yd up at
    /// the target's check position, under a 1.2 yd wall halfway; the 2 yd unit's line back to it is at 1.5 yd there.
    /// </summary>
    [Fact]
    public void AUnitsEye_IsItsModelCollisionHeight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (_, Player a, Player b) = TwoPlayers(world);
        using VMapWall wall = VMapWall.AcrossX(world, 100f, Y, Floor, 1.2f);
        a.Locomotion.CollisionHeight = 1.0f;

        Assert.False(a.IsWithinLineOfSight(b));
        Assert.True(b.IsWithinLineOfSight(a));
    }

    /// <summary>
    /// GetCollisionHeight multiplies by m_nativeScaleOverride, which Creature::InitEntry sets to the scale it chose (Creature.cpp:399-404):
    /// a creature drawn at scale 2 without model data looks from 4 yd, over a 2.5 yd wall 5 yd in front of it (its line is at 3.25 yd
    /// there). The same creature at its display's scale looks from 2 yd and the wall hides the target (1.75 yd there).
    /// </summary>
    [Fact]
    public void ACreaturesEye_IsScaledByTheScaleItWasCreatedWith()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Map map, _, Player target) = TwoPlayers(world);
        Creature big = AddCreature(map, 990_801, 90f, scale: 2f);
        Creature normal = AddCreature(map, 990_802, 90f, scale: 0f);
        using VMapWall wall = VMapWall.AcrossX(world, 95f, Y, Floor, 2.5f);

        Assert.True(big.IsWithinLineOfSight(target));
        Assert.False(normal.IsWithinLineOfSight(target));
    }

    /// <summary>
    /// ResetTransformScale sets m_nativeScaleOverride back to m_nativeScale (Unit.cpp:10978-10986), which Creature::InitEntry set to the scale
    /// it chose (Creature.cpp:403-404): a scale-2 creature looks from 4 yd over the 2.5 yd wall of the test above, from 1.6 yd in a 0.8 form
    /// (1.45 yd at the wall) and from 4 yd again once reset, not from the 2 yd of a scale of 1.
    /// </summary>
    [Fact]
    public void AResetTransformation_ReturnsToTheScaleTheUnitWasCreatedWith()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Map map, _, Player target) = TwoPlayers(world);
        Creature big = AddCreature(map, 990_805, 90f, scale: 2f);
        using VMapWall wall = VMapWall.AcrossX(world, 95f, Y, Floor, 2.5f);

        Assert.True(big.IsWithinLineOfSight(target));
        TransformScale.Set(big, 0.8f);
        Assert.False(big.IsWithinLineOfSight(target));
        TransformScale.Reset(big);
        Assert.True(big.IsWithinLineOfSight(target));
    }

    /// <summary>
    /// SetTransformScale sets m_nativeScaleOverride (Unit.cpp:10967-10976): in a 0.8-scale form (cat, SpellAuras.cpp:2463-2470) a 2 yd unit
    /// looks from 1.6 yd, under a 1.4 yd wall halfway (1.3 yd there); reset to its own scale it looks from 2 yd again (1.5 yd there).
    /// </summary>
    [Fact]
    public void ATransformedUnitsEye_IsScaledByTheTransformation()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (_, Player a, Player b) = TwoPlayers(world);
        using VMapWall wall = VMapWall.AcrossX(world, 100f, Y, Floor, 1.4f);

        TransformScale.Set(a, 0.8f);
        Assert.False(a.IsWithinLineOfSight(b));

        TransformScale.Reset(a);
        Assert.True(a.IsWithinLineOfSight(b));
    }

    /// <summary>
    /// WorldObject::IsWithinLOS (Object.h:607-610) raises the point by its default targetHeight of 2 and the viewer by its own height: from a
    /// 1 yd eye the line is at 1.5 yd at a 1.7 yd wall halfway; from a 2 yd eye it stays at 2 yd.
    /// </summary>
    [Fact]
    public void ObjectToPoint_RaisesThePointTwoYards_AndTheViewerByItsHeight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Map map, Player a, _) = TwoPlayers(world);
        using VMapWall wall = VMapWall.AcrossX(world, 100f, Y, Floor, 1.7f);

        a.Locomotion.CollisionHeight = 1.0f;
        Assert.False(map.Collision.IsWithinLineOfSight(a, 110f, Y, Floor));

        a.Locomotion.CollisionHeight = 2.0f;
        Assert.True(map.Collision.IsWithinLineOfSight(a, 110f, Y, Floor));
    }

    /// <summary>
    /// An object that is not a unit looks from 1 yd (IsWithinLOSAtPosition, Object.cpp:1770), and a game object without model bounds is
    /// looked at 1 yd up (GameObject::GetLosCheckPosition, GameObject.cpp:2421-2422): from the game object the line runs flat at 1 yd
    /// under a 1.4 yd wall halfway; from the player (2 yd) down to it the line is at 1.5 yd there.
    /// </summary>
    [Fact]
    public void AGameObject_LooksFromOneYard_AndIsLookedAtOneYardUp()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Map map, _, Player b) = TwoPlayers(world);
        var template = new GameObjectTemplate
        {
            Entry = 990_803, Type = (uint)GameObjectType.Generic, DisplayId = 1, Name = "Crate", Data = new uint[GameObjectTemplate.DataCount],
        };
        var crate = new GameObject(1, template, spawn: null);
        crate.SetPosition(90f, Y, Floor, 0f);
        map.AddObject(crate);
        using VMapWall wall = VMapWall.AcrossX(world, 100f, Y, Floor, 1.4f);

        Assert.False(map.Collision.IsWithinLineOfSight(crate, b));
        Assert.True(b.IsWithinLineOfSight(crate));
    }

    /// <summary>
    /// IsWithinLOSInMap answers true before any ray for objects within each other's bounding radius (IsWithinDist(obj, 0), Object.cpp:1759-1760;
    /// SizeFactor::BoundingRadius, :1626-1631): two players 0.6 yd apart (0.389 yd radius each) see each other through a 5 yd wall between them.
    /// </summary>
    [Fact]
    public void ObjectsWithinEachOthersBoundingRadius_AreInSight_ThroughAWall()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Map map, Player a, Player b) = TwoPlayers(world, ax: 99.7f, bx: 100.3f);
        using VMapWall wall = VMapWall.AcrossX(world, 100f, Y, Floor, 5f);

        Assert.True(a.IsWithinLineOfSight(b));
        Assert.True(b.IsWithinLineOfSight(a));
        Assert.False(map.Collision.IsWithinLineOfSight(a, b.X, b.Y, b.Z));
    }

    /// <summary>
    /// WorldObject::MovePositionToFirstCollision casts its rays at the unit's collision height (halfHeight, Object.cpp:2152 and :2177-2182):
    /// a 1 yd caster's ray stops at a 1.5 yd wall that a 2 yd caster's passes over.
    /// </summary>
    [Fact]
    public void ASummonPosition_CastsItsRayAtTheCastersCollisionHeight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (_, Player a, _) = TwoPlayers(world);
        using VMapWall wall = VMapWall.AcrossX(world, 95f, Y, Floor, 1.5f);

        a.Locomotion.CollisionHeight = 1.0f;
        Vector3 shortCaster = SummonPosition.Resolve(a, 10f, 0f);
        Assert.True(shortCaster.X < 95f, $"stopped before the wall, at {shortCaster}");

        a.Locomotion.CollisionHeight = 2.0f;
        Assert.Equal(100f, SummonPosition.Resolve(a, 10f, 0f).X, 2);
    }
}

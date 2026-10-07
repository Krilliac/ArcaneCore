using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class MinionDestinationTests
{
    private const uint Summon = 950032;
    private static PetTestKit Kit(float radius = 8) => new([
        Spell(Summon, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)GuardianEntry,
            targetA: SpellImplicitTarget.LocationUnitMinionPosition) with { Radius = radius })]);

    [Fact]
    public void Location32CreatesActualSummonAtVmangosFrontLeftOffset()
    {
        using var kit = Kit();
        var (caster, _) = kit.AddPlayer(1, 10, 20);
        caster.Orientation = 0;
        Assert.True(kit.Spells.System.IsRegisteredLocationTarget(SpellImplicitTarget.LocationUnitMinionPosition));
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Summon));
        var summon = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(10 + 8 * MathF.Cos(MathF.PI / 4), summon.X, 3);
        Assert.Equal(20 + 8 * MathF.Sin(MathF.PI / 4), summon.Y, 3);
        Assert.Equal(caster.Z, summon.Z, 3);
    }

    [Fact]
    public void Location32PreparesDestinationWhenTargetBSelectsCaster()
    {
        using var kit = new PetTestKit([Spell(Summon, Effect(SpellEffectName.SummonGuardian, 1,
            misc: (int)GuardianEntry, targetA: SpellImplicitTarget.LocationUnitMinionPosition,
            targetB: SpellImplicitTarget.UnitCaster) with { Radius = 8 })]);
        var (caster, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Summon));
        var summon = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(8 * MathF.Cos(MathF.PI / 4), summon.X, 3);
        Assert.Equal(8 * MathF.Sin(MathF.PI / 4), summon.Y, 3);
    }

    [Fact]
    public void ClientDestinationIsPreservedAndZeroRadiusStaysAtCaster()
    {
        using (var kit = Kit())
        {
            var (caster, _) = kit.AddPlayer(1, 10, 20);
            var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (30, 40, 5) };
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Summon, targets));
            var summon = Assert.Single(kit.Creatures.Creatures);
            Assert.Equal((30f, 40f, 5f), (summon.X, summon.Y, summon.Z));
        }
        using (var kit = Kit(0))
        {
            var (caster, _) = kit.AddPlayer(1, 10, 20);
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(caster, Summon));
            var summon = Assert.Single(kit.Creatures.Creatures);
            Assert.Equal((caster.X, caster.Y, caster.Z), (summon.X, summon.Y, summon.Z));
        }
    }

    [Fact]
    public void CliffAndInvalidRadiusDoNotPlaceSummonBelowCaster()
    {
        using var kit = Kit();
        var (caster, _) = kit.AddPlayer(1);
        WorldCollision.Of(kit.Spells.World).Install(lineOfSight: new CliffFloor());
        Vector3 position = SummonPosition.Resolve(caster, 8, 0);
        Assert.InRange(position.X, 0, 2);
        Assert.Equal(caster.Z, position.Z, 3);
        Assert.Equal(new Vector3(caster.X, caster.Y, caster.Z), SummonPosition.Resolve(caster, float.NaN, 0));
    }

    [Fact]
    public void SummonDestinationUsesReachablePathEndAndWallRetraction()
    {
        using var kit = Kit();
        var (caster, _) = kit.AddPlayer(1);
        WorldCollision.Of(kit.Spells.World).Install(lineOfSight: new WallFloor(), pathfinder: new ReachablePath());
        Vector3 position = SummonPosition.Resolve(caster, 8, 0);
        Assert.Equal(2f, position.X, 3); // Wall hit is returned with the requested one-yard retraction.
        Assert.Equal(0f, position.Y, 3);
        Assert.Equal(caster.Z, position.Z, 3);
    }

    private sealed class ReachablePath : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => new(PathType.Incomplete, [start, new Vector3(4, 0, 83.5f)]);
    }

    private sealed class WallFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            if (from.X == to.X) return false;
            Assert.Equal(-1f, modifyDistance);
            Assert.Equal(4f, to.X);
            hit = new Vector3(2, 0, 85.5f);
            return true;
        }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.5f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }

    private sealed class CliffFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => x > 2 ? 60 : 83.5f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>The spell cast check's line-of-sight hook (<see cref="SpellLineOfSight"/>).</summary>
public sealed class SpellLineOfSightTests
{
    [Fact]
    public void Spell_BlockedByAWall_FailsWithLineOfSight_UnlessTriggeredOrIgnoringLos()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.World.RunTick(0);
        kit.Spellbook.Teach(caster, CastBolt);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        WorldCollision.Of(kit.World).Install(new CollisionSeamTests.WallAtX(5));

        Assert.Equal(SpellCastResult.LineOfSight, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(SpellCastResult.CastOk, SpellLineOfSight.Check(caster, kit.Store.Get(CastBolt)!, target, triggered: true));
        SpellInfo ignoring = kit.Store.Get(CastBolt)! with { AttributesEx2 = SpellLineOfSight.IgnoreLineOfSight };
        Assert.Equal(SpellCastResult.CastOk, SpellLineOfSight.Check(caster, ignoring, target, triggered: false));
        Assert.Equal(SpellCastResult.LineOfSight, SpellLineOfSight.CheckDest(caster, kit.Store.Get(CastBolt)!, 10, 0, 0, triggered: false));
        Assert.Equal(SpellCastResult.CastOk, SpellLineOfSight.CheckDest(caster, kit.Store.Get(CastBolt)!, 4, 0, 0, triggered: false));

        WorldCollision.Of(kit.World).Install(OpenLineOfSight.Instance);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
    }
}

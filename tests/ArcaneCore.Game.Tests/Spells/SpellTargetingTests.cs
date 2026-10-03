using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Area, cone, chain, party/raid and line-of-sight target selection (SpellSystem.Targeting).</summary>
public sealed class SpellTargetingTests
{
    private const uint AoeSrc = 900300;
    private const uint AoeDest = 900301;
    private const uint Cone = 900302;
    private const uint ChainBolt = 900303;
    private const uint ChainHeal = 900304;
    private const uint PartyBuff = 900305;
    private const uint RaidArea = 900306;
    private const uint CappedAoe = 900307;
    private const uint IgnoreLos = 900308;
    private const uint FriendAoe = 900309;

    [Fact]
    public void SourceArea_HitsLivingEnemiesInRadius_OnlyOnce()
    {
        using var kit = Kit(out FakeRelations relations, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player near = Enemy(kit, relations, 2, 5);
        Player edge = Enemy(kit, relations, 3, 7.5f);
        Player far = Enemy(kit, relations, 4, 12);
        Player dead = Enemy(kit, relations, 5, 3);
        dead.Health = 0;
        (Player friend, _) = kit.AddPlayer(6, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, AoeSrc, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(50u, near.Health);
        Assert.Equal(50u, edge.Health);
        Assert.Equal(60u, far.Health);
        Assert.Equal(0u, dead.Health);
        Assert.Equal(60u, friend.Health);
        Assert.Equal(60u, caster.Health);
    }

    [Fact]
    public void DestinationArea_CentresOnTheClientDestination_AndChecksItsLineOfSight()
    {
        using var kit = Kit(out FakeRelations relations, out FakeLineOfSight los, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player atDest = Enemy(kit, relations, 2, 20);
        Player nearCaster = Enemy(kit, relations, 3, 2);
        kit.World.RunTick(0);
        SpellCastTargets dest = Dest(21, 0, caster.Z);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, AoeDest, dest, triggered: true));
        Assert.Equal(50u, atDest.Health);
        Assert.Equal(60u, nearCaster.Health);

        // The cast check sees the destination through the vmap-los seam; triggered casts skip it
        // (vmangos), but their targets are still checked from the area centre.
        los.WallX = 10;
        Assert.Equal(SpellCastResult.LineOfSight, kit.System.CastSpell(caster, AoeDest, Dest(21, 0, caster.Z), triggered: false));
        Assert.Equal(50u, atDest.Health);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, AoeDest, Dest(21, 0, caster.Z), triggered: true));
        Assert.Equal(40u, atDest.Health);
        Assert.Equal(60u, nearCaster.Health);
    }

    [Fact]
    public void Cone_HitsOnlyEnemiesInFront()
    {
        using var kit = Kit(out FakeRelations relations, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        caster.Orientation = 0; // facing +x
        Player front = Enemy(kit, relations, 2, 5);
        Player behind = Enemy(kit, relations, 3, -5);
        kit.World.RunTick(0);

        kit.System.CastSpell(caster, Cone, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(50u, front.Health);
        Assert.Equal(60u, behind.Health);
    }

    [Fact]
    public void Area_LineOfSightFromTheCentre_FiltersUnitsBehindWalls_UnlessIgnored()
    {
        using var kit = Kit(out FakeRelations relations, out FakeLineOfSight los, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player open = Enemy(kit, relations, 2, 3);
        Player walled = Enemy(kit, relations, 3, 6);
        kit.World.RunTick(0);
        los.WallX = 5;

        kit.System.CastSpell(caster, AoeSrc, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(50u, open.Health);
        Assert.Equal(60u, walled.Health);

        kit.System.CastSpell(caster, IgnoreLos, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(40u, open.Health);
        Assert.Equal(50u, walled.Health);
    }

    [Fact]
    public void MaxAffectedTargets_CapsAnArea_ToARandomSubset()
    {
        using var kit = Kit(out FakeRelations relations, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player[] enemies = [.. Enumerable.Range(0, 5).Select(i => Enemy(kit, relations, (uint)(10 + i), 1 + i))];
        kit.World.RunTick(0);

        kit.System.CastSpell(caster, CappedAoe, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(2, enemies.Count(e => e.Health == 50));
        Assert.Equal(3, enemies.Count(e => e.Health == 60));
    }

    [Fact]
    public void ChainDamage_JumpsToTheNearestEnemy_WithTheDamageMultiplierPerJump()
    {
        using var kit = Kit(out FakeRelations relations, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player primary = Enemy(kit, relations, 2, 10);
        Player second = Enemy(kit, relations, 3, 15);
        Player alternative = Enemy(kit, relations, 4, 4, y: 8); // 10 from primary, farther than second
        Player third = Enemy(kit, relations, 5, 22);
        Player outOfReach = Enemy(kit, relations, 6, 40);
        kit.World.RunTick(0);

        kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(primary.Guid), triggered: true);

        Assert.Equal(40u, primary.Health);  // 20
        Assert.Equal(50u, second.Health);   // 20 × 0.5
        Assert.Equal(55u, third.Health);    // 20 × 0.25
        Assert.Equal(60u, alternative.Health);
        Assert.Equal(60u, outOfReach.Health);
    }

    [Fact]
    public void Chain_StopsAtLineOfSight_AndFriendsAreNeverDamaged()
    {
        using var kit = Kit(out FakeRelations relations, out FakeLineOfSight los, out _);
        (Player caster, _) = kit.AddPlayer(1);
        Player primary = Enemy(kit, relations, 2, 10);
        (Player friend, _) = kit.AddPlayer(3, 13);
        Player walled = Enemy(kit, relations, 4, 16);
        kit.World.RunTick(0);
        los.WallX = 14;

        kit.System.CastSpell(caster, ChainBolt, SpellCastTargets.ForUnit(primary.Guid), triggered: true);

        Assert.Equal(40u, primary.Health);
        Assert.Equal(60u, friend.Health);
        Assert.Equal(60u, walled.Health);
    }

    [Fact]
    public void ChainHeal_JumpsToTheMostInjuredFriend()
    {
        using var kit = Kit(out _, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        (Player primary, _) = kit.AddPlayer(2, 3);
        (Player scratched, _) = kit.AddPlayer(3, 5);
        (Player wounded, _) = kit.AddPlayer(4, 8);
        kit.World.RunTick(0);
        primary.Health = 10;
        scratched.Health = 55;
        wounded.Health = 20;

        kit.System.CastSpell(caster, ChainHeal, SpellCastTargets.ForUnit(primary.Guid), triggered: true);

        Assert.Equal(30u, primary.Health);
        Assert.Equal(30u, wounded.Health); // 20 × 0.5
        Assert.Equal(55u, scratched.Health);
    }

    [Fact]
    public void PartyTarget_RequiresAGroupMember()
    {
        using var kit = Kit(out _, out _, out FakeGroups groups);
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player member, _) = kit.AddPlayer(2, 3);
        (Player stranger, _) = kit.AddPlayer(3, 3);
        kit.Spellbook.Teach(caster, PartyBuff);
        groups.Parties.Add([caster.Guid, member.Guid]);
        member.Health = 10;
        stranger.Health = 10;

        Assert.Equal(SpellCastResult.BadTargets, kit.System.HandleCastRequest(caster, PartyBuff, SpellCastTargets.ForUnit(stranger.Guid)));
        Assert.Equal(10u, stranger.Health);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, PartyBuff, SpellCastTargets.ForUnit(member.Guid)));
        Assert.Equal(30u, member.Health);
        Assert.Contains(WorldOpcode.SmsgCastResult, Opcodes(session));
    }

    [Fact]
    public void RaidArea_HitsTheWholeRaidInRange_PartyAreaOnlyTheSubGroup()
    {
        using var kit = Kit(out _, out _, out FakeGroups groups);
        (Player caster, _) = kit.AddPlayer(1);
        (Player party, _) = kit.AddPlayer(2, 3);
        (Player raidOnly, _) = kit.AddPlayer(3, 4);
        (Player raidFar, _) = kit.AddPlayer(4, 50);
        (Player stranger, _) = kit.AddPlayer(5, 2);
        kit.World.RunTick(0);
        groups.Parties.Add([caster.Guid, party.Guid]);
        groups.Parties.Add([raidOnly.Guid, raidFar.Guid]);
        groups.Raid = true;
        foreach (Player p in new[] { caster, party, raidOnly, raidFar, stranger })
        {
            p.Health = 10;
        }

        kit.System.CastSpell(caster, RaidArea, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(30u, caster.Health);
        Assert.Equal(30u, party.Health);
        Assert.Equal(30u, raidOnly.Health);
        Assert.Equal(10u, raidFar.Health);
        Assert.Equal(10u, stranger.Health);
        Assert.True(kit.System.IsGroupMember(caster, raidOnly, raid: true));
        Assert.False(kit.System.IsGroupMember(caster, raidOnly, raid: false));
    }

    [Fact]
    public void FriendlyArea_ExcludesEnemies_AndTheCasterWithCantTargetSelf()
    {
        using var kit = Kit(out FakeRelations relations, out _, out _);
        (Player caster, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 3);
        Player enemy = Enemy(kit, relations, 3, 3);
        kit.World.RunTick(0);
        caster.Health = friend.Health = enemy.Health = 10;

        kit.System.CastSpell(caster, FriendAoe, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(30u, friend.Health);
        Assert.Equal(10u, caster.Health);
        Assert.Equal(10u, enemy.Health);
    }

    [Fact]
    public void CheckCast_ExplicitTargetBehindAWall_IsLineOfSight_UnlessTheSpellIgnoresIt()
    {
        using var kit = Kit(out _, out FakeLineOfSight los, out _);
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 10);
        kit.Spellbook.Teach(caster, CastBolt, DotSpell);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        los.WallX = 5;

        Assert.Equal(SpellCastResult.LineOfSight, kit.System.HandleCastRequest(caster, CastBolt, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(new byte[] { (byte)CastBolt, 0, 0, 0, 2, (byte)SpellCastResult.LineOfSight }, Packets(session, WorldOpcode.SmsgCastResult)[0]);
        Assert.True(los.Queries > 0);
        Assert.False(kit.System.IsInLineOfSight(kit.Store.Get(CastBolt)!, caster, target));
        Assert.True(kit.System.IsInLineOfSight(kit.Store.Get(CastBolt)!, caster, caster));

        los.WallX = float.MaxValue;
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, DotSpell, SpellCastTargets.ForUnit(target.Guid)));
    }

    private static SpellTestKit Kit(out FakeRelations relations, out FakeLineOfSight los, out FakeGroups groups)
    {
        static SpellInfo Area(uint id, SpellEffectInfo effect) => Spell(id, effect) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        SpellEffectInfo srcDamage = Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.LocationCasterSrc,
            targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 8 };
        var kit = new SpellTestKit(
            Area(AoeSrc, srcDamage),
            Area(AoeDest, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc) with { Radius = 5 })
                with { RangeIndex = 4, Range = new SpellRange(0, 40) },
            Area(Cone, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.EnumUnitsEnemyInCone24) with { Radius = 10 }),
            Area(ChainBolt, Effect(SpellEffectName.SchoolDamage, 20, SpellImplicitTarget.UnitEnemy) with { ChainTarget = 3, DamageMultiplier = 0.5f })
                with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Area(ChainHeal, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitFriendChainHeal) with { ChainTarget = 2, DamageMultiplier = 0.5f })
                with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Area(PartyBuff, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitParty)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
            Area(RaidArea, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.EnumUnitsRaidWithinCasterRange) with { Radius = 20 }),
            Area(CappedAoe, srcDamage) with { MaxAffectedTargets = 2 },
            Area(IgnoreLos, srcDamage) with { AttributesEx2 = SpellAttributesEx2.IgnoreLineOfSight },
            Area(FriendAoe, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc) with { Radius = 8 })
                with { AttributesEx = SpellAttributesEx.CantTargetSelf });
        relations = new FakeRelations();
        los = new FakeLineOfSight();
        groups = new FakeGroups();
        kit.System.Relations = relations;
        WorldCollision.Of(kit.World).Install(los);
        kit.System.Groups = groups;
        return kit;
    }

    private static Player Enemy(SpellTestKit kit, FakeRelations relations, uint guid, float x, float y = 0)
    {
        (Player player, _) = kit.AddPlayer(guid, x, y);
        relations.Hostile.Add(player.Guid);
        return player;
    }

    private static SpellCastTargets Dest(float x, float y, float z)
        => new() { Mask = SpellCastTargetFlags.DestLocation, Dest = (x, y, z) };
}

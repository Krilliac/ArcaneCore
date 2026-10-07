using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Totems;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.ClassSpells.Totems;

/// <summary>Active totems: vmangos AI/TotemAI.cpp:66-125, Maps/GridNotifiers.h:880-898.</summary>
public sealed class ActiveTotemTests
{
    [Fact]
    public void ActiveTotem_CastsNormallyAtNearestEnemy_WithoutRestartingOrMoving()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Player near = Enemy(kit, 2, 10);
        Player far = Enemy(kit, 3, 18);
        Creature totem = Summon(kit, owner);
        (float x, float y) = (totem.X, totem.Y);
        Assert.Null(kit.Spells.GetState(totem.Guid)?.CurrentCast);

        kit.Advance(100);
        SpellCast cast = Assert.IsType<SpellCast>(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        Assert.Equal(near.Guid, cast.Targets.Unit);
        Assert.False(cast.IsTriggered);
        Assert.Equal(1000, cast.CastTime);
        kit.Advance(500);
        Assert.Same(cast, kit.Spells.GetState(totem.Guid)?.CurrentCast);
        Assert.Equal(500, cast.Timer);
        Assert.Equal(100u, near.Health);
        kit.Advance(500);

        Assert.Equal(85u, near.Health);
        Assert.Equal(100u, far.Health);
        Assert.NotSame(cast, kit.Spells.GetState(totem.Guid)?.CurrentCast);
        Assert.Equal((x, y), (totem.X, totem.Y));
        Assert.True(totem.Movement.Flags.HasFlag(MovementFlags.Root));
        Assert.IsType<NullCreatureAI>(totem.AI);
        Assert.Null(totem.Combat.Victim);
    }

    [Fact]
    public void OwnerCombatTarget_IsPreferredToNearerEnemy_ThenVictimRemainsSticky()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Player near = Enemy(kit, 2, 8);
        Player far = Enemy(kit, 3, 18);
        Assert.True(kit.Map.Combat.Attack(owner, far, melee: false));
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        Assert.Equal(far.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
        kit.Map.Combat.AttackStop(owner);
        kit.Advance(1000);
        Assert.Equal(far.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
        Assert.Equal(100u, near.Health);
    }

    [Fact]
    public void OwnerAttacker_IsPreferred_WhenOwnerHasNoVictim()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        _ = Enemy(kit, 2, 8);
        Player attacker = Enemy(kit, 3, 18);
        Assert.True(kit.Map.Combat.Attack(attacker, owner, melee: false));
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        Assert.Equal(attacker.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("dead")]
    [InlineData("flags")]
    [InlineData("map")]
    public void InvalidOldVictim_IsReplaced_AfterCurrentCastFinishes(string reason)
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Player near = Enemy(kit, 2, 8);
        Player far = Enemy(kit, 3, 18);
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        switch (reason)
        {
            case "range": near.Relocate(100, 0, near.Z, 0, 0); break;
            case "dead": near.Health = 0; break;
            case "flags": near.UnitFlags |= UnitFlags.NotSelectable; break;
            case "map": kit.World.RemovePlayer(near); break;
        }

        kit.Advance(1100);
        Assert.Equal(far.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
    }

    [Fact]
    public void FriendlyUnflaggedAndHiddenPlayers_AreSkipped_AndNearestBlockedEnemyPreventsCast()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        _ = kit.AddPlayer(2, 4).Player;
        Player unflagged = Enemy(kit, 3, 6);
        unflagged.Flags &= ~PlayerFlags.PvpDesired;
        unflagged.UnitFlags &= ~UnitFlags.Pvp;
        Player hidden = Enemy(kit, 4, 8);
        Player blocked = Enemy(kit, 5, 10);
        Player reachable = Enemy(kit, 6, 5, 15);
        var registry = new StealthRegistry();
        registry.SetVisibility(hidden, StealthVisibility.NoDetect);
        StealthServices.Install(kit.Map, new StealthServices(kit.Spells, registry, new StealthOptions()));
        WorldCollision.Of(kit.World).Install(new FakeLineOfSight { WallX = 9 });
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        Assert.Null(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        Assert.Equal(100u, blocked.Health);
        blocked.Relocate(100, 0, blocked.Z, 0, 0);
        kit.Advance(100);
        Assert.Equal(reachable.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
    }

    [Fact]
    public void NormalStealthPlayer_IsNotDetectedEvenAtPointBlankRange()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Creature totem = Summon(kit, owner);
        Player hidden = Enemy(kit, 2, totem.X, totem.Y);
        var registry = new StealthRegistry();
        registry.SetVisibility(hidden, StealthVisibility.Stealth);
        StealthServices.Install(kit.Map, new StealthServices(kit.Spells, registry, new StealthOptions()));
        kit.Advance(1100);
        Assert.Null(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        Assert.Equal(100u, hidden.Health);
    }

    [Fact]
    public void DeadOwner_PreventsBoltLandingWhenSpellUpdateRunsBeforeLifecycleOnLongTick()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Player enemy = Enemy(kit, 2, 8);
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        Assert.NotNull(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        owner.Health = 0;
        kit.Advance(1500, step: 1500);
        Assert.False(totem.IsInWorld);
        Assert.Null(kit.Spells.GetState(totem.Guid));
        Assert.Equal(100u, enemy.Health);
    }

    [Fact]
    public void UnflaggedOwner_DoesNotAcquireFlaggedEnemyPlayerAutomatically()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        owner.Flags &= ~PlayerFlags.PvpDesired;
        owner.UnitFlags &= ~UnitFlags.Pvp;
        _ = Enemy(kit, 2, 8);
        Creature totem = Summon(kit, owner);
        kit.Advance(2000);
        Assert.Null(kit.Spells.GetState(totem.Guid)?.CurrentCast);
    }

    [Fact]
    public void RangeBoundary_IsStrictAndIncludesBoundingRadii()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Creature totem = Summon(kit, owner);
        Player enemy = Enemy(kit, 2, 100);
        float reach = 20 + totem.BoundingRadius + enemy.BoundingRadius;
        enemy.Relocate(totem.X, totem.Y + reach, totem.Z, 0, 0);
        kit.Advance(100);
        Assert.Null(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        enemy.Relocate(totem.X, totem.Y + reach - 0.01f, totem.Z, 0, 0);
        kit.Advance(100);
        Assert.Equal(enemy.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
    }

    [Fact]
    public void ActiveTotem_CanCastAtCreatureTargets_ThroughConfiguredResolver()
    {
        using var kit = new TotemKit();
        Player owner = Owner(kit);
        Creature enemy = kit.Creatures.SpawnTemporary(CreatureTestSupport.Template(CreatureTestSupport.WolfEntry), 8, 0, owner.Z, 0);
        enemy.MaxHealth = enemy.Health = 100;
        kit.Relations.Hostile.Add(enemy.Guid);
        kit.Spells.Units = new AllMapUnits();
        Creature totem = Summon(kit, owner);
        kit.Advance(1100);
        Assert.Equal(85u, enemy.Health);
        Assert.Equal(enemy.Guid, kit.Spells.GetState(totem.Guid)!.CurrentCast!.Targets.Unit);
    }

    [Theory]
    [InlineData("death")]
    [InlineData("logout")]
    [InlineData("replace")]
    [InlineData("expiry")]
    public void Unsummon_CancelsInFlightCast_WithoutLateDamage(string reason)
    {
        using var kit = new TotemKit(summonDurationMs: reason == "expiry" ? 500 : 60_000);
        Player owner = Owner(kit);
        Player enemy = Enemy(kit, 2, 8);
        Creature totem = Summon(kit, owner);
        kit.Advance(100);
        Assert.NotNull(kit.Spells.GetState(totem.Guid)?.CurrentCast);
        switch (reason)
        {
            case "death": owner.Health = 0; break;
            case "logout": kit.World.RemovePlayer(owner); break;
            case "replace": kit.Cast(owner, TotemKit.FireTotemSummon); break;
        }

        kit.Advance(1500);
        Assert.False(totem.IsInWorld);
        Assert.Null(kit.Spells.GetState(totem.Guid));
        Assert.Equal(100u, enemy.Health);
    }

    private static Player Owner(TotemKit kit)
    {
        Player owner = kit.AddPlayer(1).Player;
        kit.Map.Combat.TogglePvp(owner, desired: true);
        return owner;
    }

    private static Player Enemy(TotemKit kit, uint guid, float x, float y = 0)
    {
        Player enemy = kit.AddPlayer(guid, x, y).Player;
        enemy.SetByte(UpdateFields.UnitFieldBytes0, 0, 2); // orc: opposite faction from TestWorld's human
        kit.Map.Combat.TogglePvp(enemy, desired: true);
        enemy.MaxHealth = enemy.Health = 100;
        kit.Relations.Hostile.Add(enemy.Guid);
        return enemy;
    }

    private static Creature Summon(TotemKit kit, Player owner)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, TotemKit.ActiveSummon));
        return Assert.IsType<Creature>(kit.Totems.GetTotem(owner, TotemSlot.Fire));
    }

    private sealed class AllMapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.FindObject(guid) as Unit;
    }
}

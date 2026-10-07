using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// "Player controlled" in the white swing is vmangos Unit::IsCharmerOrOwnerPlayerOrPlayerItself, not "is a player": glancing blows
/// (SpellCaster.cpp:548) and HITINFO_PVP (Unit::SetDamageIndependentHitInfoFlags, Unit.cpp:1606-1613) count a player's pet or charmed
/// unit as the player.
/// </summary>
public sealed class MeleePlayerControlledTests
{
    private static (WorldRuntime World, Map Map, Player Owner, CombatTestUnit Pet, CombatTestUnit Mob) Scene()
    {
        (WorldRuntime world, Map map, ScriptedRandom _, TestCombatHooks _) = CombatTestKit.CreateWorld();
        Player owner = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        var pet = new CombatTestUnit();
        pet.Spawn(map, 1, 0);
        pet.SetOwnerGuid(owner.Guid);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 3, 0, orientation: MathF.PI);
        return (world, map, owner, pet, mob);
    }

    [Fact]
    public void APlayersPet_IsPlayerControlledAsAnAttacker_SoItsSwingsCanGlance()
    {
        (WorldRuntime world, Map map, Player _, CombatTestUnit pet, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;

        MeleeRollInput input = map.Combat.BuildRollInput(pet, mob, WeaponAttackType.BaseAttack);

        Assert.True(input.AttackerIsPlayerControlled);
        Assert.False(input.VictimIsPlayerControlled);
    }

    [Fact]
    public void APlayersPet_IsPlayerControlledAsAVictim_SoPlayersDoNotGlanceOnIt()
    {
        (WorldRuntime world, Map map, Player _, CombatTestUnit pet, CombatTestUnit _) = Scene();
        using WorldRuntime _ = world;
        Player enemy = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);

        MeleeRollInput input = map.Combat.BuildRollInput(enemy, pet, WeaponAttackType.BaseAttack);

        Assert.True(input.VictimIsPlayerControlled);
    }

    [Fact]
    public void APetHittingAPlayer_SetsHitInfoPvp()
    {
        (WorldRuntime world, Map map, Player _, CombatTestUnit pet, CombatTestUnit _) = Scene();
        using WorldRuntime _ = world;
        Player enemy = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);

        MeleeDamageInfo hit = map.Combat.CalculateMeleeDamage(pet, enemy, WeaponAttackType.BaseAttack);

        Assert.True(hit.HitInfo.HasFlag(HitInfo.Pvp));
    }

    [Fact]
    public void AnUnownedCreatureHittingAPlayer_DoesNotSetHitInfoPvp()
    {
        (WorldRuntime world, Map map, Player owner, CombatTestUnit _, CombatTestUnit mob) = Scene();
        using WorldRuntime _ = world;

        MeleeDamageInfo hit = map.Combat.CalculateMeleeDamage(mob, owner, WeaponAttackType.BaseAttack);

        Assert.False(hit.HitInfo.HasFlag(HitInfo.Pvp));
    }
}

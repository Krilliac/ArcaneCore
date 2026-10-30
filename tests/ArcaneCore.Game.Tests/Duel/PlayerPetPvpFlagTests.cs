using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// The PvP-flag rule applies through the player behind each unit (vmangos Unit::IsValidAttackTarget reads
/// GetAffectingPlayer on both sides, Object.cpp:3797-3815): a pet, guardian or totem of an unflagged enemy player is
/// not attackable by a player until that player is PvP flagged or in a started duel with the attacker.
/// </summary>
public sealed class PlayerPetPvpFlagTests
{
    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
    ]);

    public static TheoryData<string> HookKinds => new() { "default", "faction" };

    private static CombatHooks Hooks(Map map, string kind)
    {
        CombatHooks hooks = kind == "faction" ? new FactionCombatHooks(Catalog) : CombatHooks.Default;
        map.Combat.Hooks = hooks;
        return hooks;
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void PetOfAnUnflaggedEnemyPlayer_IsNotAttackableByAPlayer(string kind)
    {
        var (world, map, _, a, h, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);
        var pet = new OwnedUnit { Owner = h };
        pet.Spawn(map, 11, 10);

        Assert.False(hooks.CanAttack(a, h));   // control: the owner itself is already refused
        Assert.False(hooks.CanAttack(a, pet));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void PetOfAFlaggedEnemyPlayer_IsAttackable(string kind)
    {
        var (world, map, _, a, h, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);
        var pet = new OwnedUnit { Owner = h };
        pet.Spawn(map, 11, 10);

        h.UnitFlags |= UnitFlags.Pvp;

        Assert.True(hooks.CanAttack(a, h));
        Assert.True(hooks.CanAttack(a, pet));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void PetOfAnUnflaggedDuelOpponent_IsAttackable(string kind)
    {
        var (world, map, _, a, h, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);
        var pet = new OwnedUnit { Owner = h };
        pet.Spawn(map, 11, 10);

        DuelTestKit.Link(a, h, startTime: 100);

        Assert.True(hooks.CanAttack(a, pet));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void PetOfAPlayer_CannotAttackAnUnflaggedEnemyPlayersPet(string kind)
    {
        var (world, map, _, a, h, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);
        var mine = new OwnedUnit { Owner = a };
        var theirs = new OwnedUnit { Owner = h };
        mine.Spawn(map, 11, 10);
        theirs.Spawn(map, 12, 10);
        mine.UnitFlags |= UnitFlags.PlayerControlled; // as a real pet carries it, so the template rule is the player-involved one (Object.cpp:3767-3769)
        theirs.UnitFlags |= UnitFlags.PlayerControlled;

        Assert.False(hooks.CanAttack(mine, theirs));
        Assert.False(hooks.CanAttack(mine, h));

        h.UnitFlags |= UnitFlags.Pvp;

        Assert.True(hooks.CanAttack(mine, theirs));
    }

    [Theory]
    [MemberData(nameof(HookKinds))]
    public void OwnerlessCreature_IsStillAttackable(string kind)
    {
        var (world, map, _, a, _, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);
        var wolf = new OwnedUnit(); // no owner: a plain creature, the flag rule must not touch it
        wolf.Spawn(map, 11, 10);

        Assert.True(hooks.CanAttack(a, wolf));
    }

    [Fact]
    public void PetHittingAFlaggedEnemyPlayer_PvpFlagsAndContestsItsOwner()
    {
        // vmangos TogglePlayerPvPFlagOnAttackVictim / SetInCombatWithAggressor act on GetAffectingPlayer (Unit.cpp:5959-6053).
        var (world, map, _, a, h, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        Hooks(map, "default");
        var pet = new OwnedUnit { Owner = a };
        pet.Spawn(map, 11, 10);
        h.UnitFlags |= UnitFlags.Pvp;
        Assert.Equal(0u, (uint)(a.UnitFlags & UnitFlags.Pvp));

        map.Combat.DealDamage(pet, h, 5);

        Assert.NotEqual(0u, (uint)(a.UnitFlags & UnitFlags.Pvp));
        Assert.True(a.Combat.InPvpCombat);
        Assert.NotEqual(0u, (uint)(a.Flags & PlayerFlags.ContestedPvp));
    }
}

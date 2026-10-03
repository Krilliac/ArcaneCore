using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Duel;

/// <summary>
/// A started duel makes the two players hostile regardless of team or PvP flag (vmangos Object.cpp:3650-3652 reaction,
/// :3797-3800 IsValidAttackTarget) and removes the PvP pulses between them (Unit.cpp:5973, 6047).
/// </summary>
public sealed class DuelHostilityTests
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
    public void SameTeamPlayers_AreFriendlyUntilTheDuelStarts_ThenHostileBothWays(string kind)
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        CombatHooks hooks = Hooks(map, kind);

        Assert.True(hooks.IsFriendly(a, b));
        Assert.False(hooks.CanAttack(a, b));

        (DuelInfo da, DuelInfo db) = DuelTestKit.Link(a, b); // requested, not started
        Assert.True(hooks.IsFriendly(a, b));
        Assert.False(hooks.CanAttack(a, b));
        Assert.False(hooks.CanAttack(b, a));

        da.StartTimeSeconds = db.StartTimeSeconds = 100; // started
        Assert.False(hooks.IsFriendly(a, b));
        Assert.False(hooks.IsFriendly(b, a));
        Assert.True(hooks.CanAttack(a, b));
        Assert.True(hooks.CanAttack(b, a));
    }

    [Fact]
    public void EnemyTeamDuelist_NeedsNoPvpFlag()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        Assert.False(map.Combat.Hooks.CanAttack(a, b)); // not flagged, no duel

        DuelTestKit.Link(a, b, startTime: 100);

        Assert.True(map.Combat.Hooks.CanAttack(a, b));
        Assert.True(map.Combat.Hooks.CanAttack(b, a));
    }

    [Fact]
    public void FinishedDuel_IsNoLongerHostile_ButStillCountsAsInDuelWith()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        (DuelInfo da, DuelInfo db) = DuelTestKit.Link(a, b, startTime: 100);
        da.Finished = db.Finished = true;

        Assert.False(map.Combat.Hooks.CanAttack(a, b));
        Assert.True(map.Combat.Hooks.IsFriendly(a, b));
        Assert.True(DuelRules.IsInDuelWith(a, b)); // vmangos Player.h:2210 does not test finished
        Assert.False(DuelRules.IsOpponentHostile(a, b));
    }

    [Fact]
    public void ThirdPlayerOnTheSameTeam_StaysFriendly()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        Player c = CombatTestKit.AddPlayer(world, 3, 14, 10, new FakeSession(3));
        DuelTestKit.Link(a, b, startTime: 100);

        Assert.True(map.Combat.Hooks.IsFriendly(a, c));
        Assert.False(map.Combat.Hooks.CanAttack(a, c));
        Assert.False(map.Combat.Hooks.CanAttack(c, b));
    }

    [Fact]
    public void DuelDoesNotOverrideTheNonAttackableChecks()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        DuelTestKit.Link(a, b, startTime: 100);

        b.UnitFlags |= UnitFlags.NotSelectable;
        Assert.False(map.Combat.Hooks.CanAttack(a, b));
        b.UnitFlags &= ~UnitFlags.NotSelectable;

        Assert.True(map.Combat.Hooks.CanAttack(a, b));
        b.UnitFlags |= UnitFlags.NonAttackable2;
        Assert.False(map.Combat.Hooks.CanAttack(a, b));
    }

    [Fact]
    public void PetOfADuelist_IsHostileToTheOpponent_ThroughTheSeamInterface()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        DuelTestKit.Link(a, b, startTime: 100);
        var pet = new OwnedUnit { Owner = a };
        pet.Spawn(map, 11, 10);

        Assert.True(DuelRules.IsOpponentHostile(pet, b));
        Assert.True(DuelRules.IsOpponentHostile(b, pet));
        Assert.False(DuelRules.IsOpponentHostile(pet, a));
    }

    [Fact]
    public void DuelOpponents_DoNotPulseThePvpFlag_ButAThirdPartyStillDoes()
    {
        var (world, map, _, a, b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        Player c = CombatTestKit.AddPlayer(world, 3, 14, 10, new FakeSession(3), Race.Orc);
        a.UnitFlags |= UnitFlags.Pvp;
        b.UnitFlags |= UnitFlags.Pvp;
        c.UnitFlags |= UnitFlags.Pvp;
        DuelTestKit.Link(a, b, startTime: 100);

        map.Combat.DealDamage(a, b, 5);

        Assert.False(a.Combat.InPvpCombat);
        Assert.False(b.Combat.InPvpCombat);

        map.Combat.DealDamage(a, c, 5); // control: the pulse still works against a non-opponent

        Assert.True(a.Combat.InPvpCombat);
        Assert.True(c.Combat.InPvpCombat);
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Duel;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Free-for-all hostility through the production hook chain (<see cref="ReputationCombatHooks"/> over <see cref="FactionCombatHooks"/>): two
/// ungrouped players who both carry the free-for-all flag, and the pets, traps and mind-controlled units acting for them, are hostile whatever
/// their teams (vmangos GetReactionTo, Object.cpp:3654-3660: same group first, then both IsFFAPvP). Unproven until the integration gate runs.
/// </summary>
public sealed class FreeForAllHostilityTests
{
    private sealed class Rig : IDisposable
    {
        private readonly WorldRuntime _world;

        public Rig(Func<Player, Player, bool>? sameRaid = null)
        {
            (_world, Map map, _, _) = CombatTestKit.CreateWorld();
            Map = map;
            var service = new ReputationService(Factions, roll: () => 0.0);
            Resolver = new ReputationReactionResolver(Templates, Factions, service.For, sameRaid);
            Hooks = new ReputationCombatHooks(new FactionCombatHooks(Templates), Resolver);
            Map.Combat.Hooks = Hooks;
            A = Add(1, 10);
            B = Add(2, 12);
            C = Add(3, 14);
            foreach (Player player in new[] { A, B, C })
            {
                service.Track(player, service.Create(player, ArcaneCore.Kernel.Reputation.CharacterReputationData.Empty));
            }
        }

        public Map Map { get; }

        public ReputationReactionResolver Resolver { get; }

        public ReputationCombatHooks Hooks { get; }

        public WorldRuntime World => _world;

        public Player A { get; }

        public Player B { get; }

        public Player C { get; }

        private Player Add(uint id, float x) => CombatTestKit.AddPlayer(_world, id, x, 10, new FakeSession((int)id), Race.Human);

        public static void Flag(params Player[] players)
        {
            foreach (Player player in players)
            {
                player.Flags |= PlayerFlags.FfaPvp;
            }
        }

        public void Dispose() => _world.Dispose();
    }

    [Fact]
    public void MindControlledSameTeamPlayer_IsAttackableByAFreeForAllPlayer_ThroughTheProductionChain()
    {
        using var rig = new Rig();
        Rig.Flag(rig.B, rig.C);
        CharmService.SetCharmerGuid(rig.A, rig.C.Guid); // A is mind controlled by C: all three are Human, so the team alone calls them friends

        Assert.True(rig.Hooks.CanAttack(rig.A, rig.B));
        Assert.True(rig.Hooks.CanAttack(rig.B, rig.A));
        Assert.True(rig.Hooks.IsHostileTo(rig.A, rig.B));
        Assert.True(rig.Resolver.TryGetReaction(rig.A, rig.B, out ArcaneCore.Game.Reputation.ReputationRank rank));
        Assert.Equal(ArcaneCore.Game.Reputation.ReputationRank.Hostile, rank);
    }

    [Fact]
    public void MindControlledSameTeamPlayer_IsNotAttackable_WhenItsControllerIsNotFreeForAll_OrIsGroupedWithTheVictim()
    {
        using (var rig = new Rig())
        {
            Rig.Flag(rig.A, rig.B); // the charmer C is not flagged: the controlling players are not a free-for-all pair
            CharmService.SetCharmerGuid(rig.A, rig.C.Guid);
            Assert.False(rig.Hooks.CanAttack(rig.A, rig.B));
        }

        using var grouped = new Rig((x, y) => true);
        Rig.Flag(grouped.B, grouped.C);
        CharmService.SetCharmerGuid(grouped.A, grouped.C.Guid);
        Assert.False(grouped.Hooks.CanAttack(grouped.A, grouped.B)); // the group clause comes before the free-for-all one (Object.cpp:3654-3660)
    }

    [Fact]
    public void PetAndTrapOwner_AreHostileToASameTeamFreeForAllPlayer_ButNotToAGroupMateOrAnUnflaggedOne()
    {
        using var rig = new Rig();
        var pet = new OwnedUnit { Owner = rig.A, FactionTemplate = rig.A.FactionTemplate };
        pet.UnitFlags |= UnitFlags.PlayerControlled;
        pet.Spawn(rig.Map, 11, 10);

        Assert.False(rig.Hooks.IsHostileTo(pet, rig.B)); // control: the same team, no flags
        Assert.False(rig.Hooks.IsHostileTo(rig.A, rig.B));

        Rig.Flag(rig.A, rig.B);
        Assert.True(rig.Hooks.IsHostileTo(rig.A, rig.B));   // a hunter trap asks the owner's hostility (TrapSystem.IsHostileTo)
        Assert.True(rig.Hooks.IsHostileTo(pet, rig.B));     // a pet picks its victim with the same test (MapUnitControl)
        Assert.True(rig.Hooks.IsHostileTo(rig.B, pet));
        Assert.True(rig.Hooks.CanAttack(rig.B, pet));
        Assert.False(rig.Hooks.IsHostileTo(pet, rig.A));    // never hostile to its own owner

        // The wrapped template-only hooks answer the same way when the reputation state of a player is not loaded.
        Assert.True(rig.Hooks.Fallback.IsHostileTo(pet, rig.B));
        Assert.True(rig.Hooks.Fallback.IsHostileTo(rig.A, rig.B));
        Assert.False(rig.Hooks.Fallback.IsHostileTo(pet, rig.A));

        using var grouped = new Rig((x, y) => true);
        Rig.Flag(grouped.A, grouped.B);
        Assert.False(grouped.Hooks.IsHostileTo(grouped.A, grouped.B));
    }

    [Fact]
    public void TheFreeForAllPvpPulse_IsSkippedBetweenTwoFreeForAllPlayers_ButNotForAThirdParty()
    {
        using var rig = new Rig();
        Rig.Flag(rig.A, rig.B);
        rig.A.UnitFlags |= UnitFlags.Pvp;
        rig.B.UnitFlags |= UnitFlags.Pvp;
        rig.C.UnitFlags |= UnitFlags.Pvp;

        rig.Map.Combat.DealDamage(rig.A, rig.B, 5);

        Assert.False(rig.A.Combat.InPvpCombat); // Unit.cpp:6047: both IsFFAPvP, no pulse
        Assert.False(rig.B.Combat.InPvpCombat);

        rig.Map.Combat.DealDamage(rig.A, rig.C, 5); // control: C is not free-for-all

        Assert.True(rig.A.Combat.InPvpCombat);
        Assert.True(rig.C.Combat.InPvpCombat);
    }
}

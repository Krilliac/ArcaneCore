using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Tests.Duel;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>
/// Reputation-aware attackability, hostility and aggro (vmangos Object.cpp:3608-3816): the same fixture factions and
/// templates as the reaction tests, over the template-only hooks the world ships.
/// </summary>
public sealed class ReputationCombatHooksTests
{
    private const uint StormwindGuard = 4;       // faction 72 (list 7, peace-forced for the Alliance), friendly to the player race
    private const uint ContestedStormwind = 10;  // faction 72 with the contested-guard template flag
    private const uint BootyBayGoblin = 9;       // faction 21 (list 0), neutral by template
    private const uint HostileMonster = 3;       // no faction, hostile mask
    private const uint NeutralCritter = 2;
    private const uint FriendlyNoFaction = 8;

    private sealed class Rig : IDisposable
    {
        public Rig(Func<Player, Player, bool>? sameRaid = null, bool track = true)
        {
            (World, Map, _, _) = CombatTestKit.CreateWorld();
            Service = new ReputationService(Factions, roll: () => 0.0);
            Resolver = new ReputationReactionResolver(Templates, Factions, Service.For, sameRaid);
            TemplateOnly = new FactionCombatHooks(Templates);
            Hooks = new ReputationCombatHooks(TemplateOnly, Resolver);
            Map.Combat.Hooks = Hooks;
            Player = CombatTestKit.AddPlayer(World, 1, 0, 0, new FakeSession(1));
            if (track)
            {
                Service.Track(Player, Service.Create(Player, CharacterReputationData.Empty));
            }
        }

        public WorldRuntime World { get; }

        public Map Map { get; }

        public ReputationService Service { get; }

        public ReputationReactionResolver Resolver { get; }

        public FactionCombatHooks TemplateOnly { get; }

        public ReputationCombatHooks Hooks { get; }

        public Player Player { get; }

        public CombatTestUnit Npc(uint template)
        {
            var npc = new CombatTestUnit { FactionTemplate = template };
            npc.Spawn(Map, 3, 0);
            return npc;
        }

        public Creature Creature(uint template)
            => new(template + 700, CreatureTestSupport.Template(template + 700, t => t.Faction = template), null, CreatureContent.Empty, new Random(1));

        public void Dispose() => World.Dispose();
    }

    [Fact]
    public void Guard_AttacksAndAggrosAHatedPlayer_WhichTheTemplateOnlyHooksNeverDid()
    {
        using var rig = new Rig();
        CombatTestUnit guard = rig.Npc(StormwindGuard);
        var hostility = new ReputationCreatureHostility(new FactionCreatureHostility(Templates), rig.Resolver);

        // Template only: the guard is friendly to the player race, so nobody may attack anybody.
        Assert.False(rig.TemplateOnly.CanAttack(rig.Player, guard));
        Assert.False(new FactionCreatureHostility(Templates).IsHostile(rig.Creature(StormwindGuard), rig.Player));
        Assert.False(rig.Hooks.CanAttack(rig.Player, guard));
        Assert.False(hostility.IsHostile(rig.Creature(StormwindGuard), rig.Player));

        rig.Service.SetReputation(rig.Player, Stormwind, ReputationMath.Bottom); // Hated: war is declared on its own
        Assert.True(rig.Service.IsAtWar(rig.Player, Stormwind));
        Assert.True(rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.True(rig.Hooks.CanAttack(rig.Player, guard));
        Assert.True(rig.Hooks.CanAttack(guard, rig.Player));
        Assert.True(hostility.IsHostile(rig.Creature(StormwindGuard), rig.Player));
    }

    [Fact]
    public void ReputationFaction_IsNotAttackableUnlessAtWar_AndNeverAggros()
    {
        using var rig = new Rig();
        CombatTestUnit goblin = rig.Npc(BootyBayGoblin);
        var hostility = new ReputationCreatureHostility(new FactionCreatureHostility(Templates), rig.Resolver);

        Assert.True(rig.TemplateOnly.CanAttack(rig.Player, goblin)); // neutral by template: attackable
        Assert.False(rig.Hooks.CanAttack(rig.Player, goblin));       // a reputation faction: friendly unless at war
        Assert.False(rig.Hooks.CanAttack(goblin, rig.Player));

        rig.Service.ModifyReputation(rig.Player, BootyBay, 1); // makes it visible; the client can then declare war
        Assert.True(rig.Service.SetAtWar(rig.Player, 0, true));
        Assert.True(rig.Hooks.CanAttack(rig.Player, goblin));
        Assert.True(rig.Hooks.CanAttack(goblin, rig.Player));
        // The goblin's own reaction toward the player is capped at Neutral while at war: it does not aggro.
        Assert.False(rig.Hooks.IsHostileTo(goblin, rig.Player));
        Assert.False(hostility.IsHostile(rig.Creature(BootyBayGoblin), rig.Player));
        Assert.True(rig.Hooks.IsHostileTo(rig.Player, goblin));

        Assert.True(rig.Service.SetAtWar(rig.Player, 0, false));
        Assert.False(rig.Hooks.CanAttack(rig.Player, goblin));
    }

    [Fact]
    public void HostileAtWarRank_BootyBayGoblin_AggrosOnceTheStandingFallsToHostile()
    {
        using var rig = new Rig();
        CombatTestUnit goblin = rig.Npc(BootyBayGoblin);
        rig.Service.SetReputation(rig.Player, BootyBay, -2000); // Unfriendly [-3000, 0)
        Assert.False(rig.Hooks.IsHostileTo(goblin, rig.Player));
        rig.Service.SetReputation(rig.Player, BootyBay, -4000); // Hostile [-6000, -3000): war declared
        Assert.True(rig.Hooks.IsHostileTo(goblin, rig.Player));
        Assert.True(rig.Hooks.CanAttack(goblin, rig.Player));
    }

    [Fact]
    public void ContestedGuard_AttacksOnlyAContestedPlayer()
    {
        using var rig = new Rig();
        CombatTestUnit guard = rig.Npc(ContestedStormwind);
        Assert.False(rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.False(rig.Hooks.CanAttack(rig.Player, guard));

        rig.Player.Flags |= PlayerFlags.ContestedPvp;
        Assert.True(rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.True(rig.Hooks.IsHostileTo(rig.Player, guard));
        Assert.True(rig.Hooks.CanAttack(rig.Player, guard));
        Assert.True(rig.Hooks.CanAttack(guard, rig.Player));
    }

    [Fact]
    public void GameMaster_ReadsNeutral_EvenWhenHated()
    {
        using var rig = new Rig();
        CombatTestUnit guard = rig.Npc(StormwindGuard);
        rig.Service.SetReputation(rig.Player, Stormwind, ReputationMath.Bottom);
        Assert.True(rig.Hooks.IsHostileTo(guard, rig.Player));
        rig.Player.Flags |= PlayerFlags.Gm;
        Assert.False(rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.True(rig.Resolver.TryGetReaction(guard, rig.Player, out ReputationRank rank));
        Assert.Equal(ReputationRank.Neutral, rank);
        Assert.True(rig.Resolver.TryGetReaction(rig.Player, guard, out rank));
        Assert.Equal(ReputationRank.Neutral, rank);
    }

    [Fact]
    public void ForcedRank_BeatsTheRealRank_BothWays()
    {
        using var rig = new Rig();
        CombatTestUnit guard = rig.Npc(StormwindGuard);
        rig.Service.SetReputation(rig.Player, Stormwind, ReputationMath.Bottom);
        rig.Service.For(rig.Player)!.SetForcedReaction(Stormwind, ReputationRank.Friendly);

        Assert.False(rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.False(rig.Hooks.CanAttack(rig.Player, guard)); // friendly in either direction denies
        Assert.False(rig.Hooks.CanAttack(guard, rig.Player));

        rig.Service.For(rig.Player)!.ClearForcedReaction(Stormwind);
        Assert.True(rig.Hooks.IsHostileTo(guard, rig.Player));

        // A forced Hostile makes an at-peace faction attackable: the forced rank also lifts the neutral-at-war rule.
        CombatTestUnit goblin = rig.Npc(BootyBayGoblin);
        rig.Service.For(rig.Player)!.SetForcedReaction(BootyBay, ReputationRank.Hostile);
        Assert.True(rig.Hooks.IsHostileTo(goblin, rig.Player));
        Assert.True(rig.Hooks.CanAttack(rig.Player, goblin));
    }

    [Fact]
    public void ForcedReaction_AlsoDrivesTheNpcReactionServicesUse()
    {
        using var rig = new Rig();
        rig.Service.SetReputation(rig.Player, Stormwind, ReputationMath.Bottom);
        Assert.True(rig.Service.TryGetNpcReaction(rig.Player, StormwindNpc, PlayerTemplate, out ReputationRank rank));
        Assert.Equal(ReputationRank.Hated, rank);
        rig.Service.For(rig.Player)!.SetForcedReaction(Stormwind, ReputationRank.Honored);
        Assert.True(rig.Service.TryGetNpcReaction(rig.Player, StormwindNpc, PlayerTemplate, out rank));
        Assert.Equal(ReputationRank.Honored, rank);
        Assert.True(rig.Service.TryGetPlayerReaction(rig.Player, StormwindNpc, PlayerTemplate, out rank));
        Assert.Equal(ReputationRank.Honored, rank);
    }

    [Fact]
    public void NonReputationFactions_KeepTheTemplateRules()
    {
        using var rig = new Rig();
        Assert.True(rig.Hooks.CanAttack(rig.Player, rig.Npc(HostileMonster)));
        Assert.True(rig.Hooks.CanAttack(rig.Player, rig.Npc(NeutralCritter)));
        Assert.False(rig.Hooks.CanAttack(rig.Player, rig.Npc(FriendlyNoFaction)));
        Assert.True(rig.Hooks.IsHostileTo(rig.Npc(HostileMonster), rig.Player));
        Assert.False(rig.Hooks.IsHostileTo(rig.Npc(NeutralCritter), rig.Player));
    }

    [Fact]
    public void WithoutLoadedStandings_ReputationFactionsFallBackToTheTemplateOnlyAnswer()
    {
        using var rig = new Rig(track: false);
        CombatTestUnit goblin = rig.Npc(BootyBayGoblin);
        CombatTestUnit guard = rig.Npc(StormwindGuard);
        Assert.Equal(rig.TemplateOnly.CanAttack(rig.Player, goblin), rig.Hooks.CanAttack(rig.Player, goblin));
        Assert.Equal(rig.TemplateOnly.CanAttack(rig.Player, guard), rig.Hooks.CanAttack(rig.Player, guard));
        Assert.Equal(rig.TemplateOnly.IsHostileTo(guard, rig.Player), rig.Hooks.IsHostileTo(guard, rig.Player));
        Assert.False(new ReputationCreatureHostility(new FactionCreatureHostility(Templates), rig.Resolver)
            .IsHostile(rig.Creature(StormwindGuard), rig.Player));
    }

    [Fact]
    public void PlayerVersusPlayer_IsLeftToTheBaseRules()
    {
        (WorldRuntime world, Map map, _, Player a, Player b, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        var service = new ReputationService(Factions, roll: () => 0.0);
        map.Combat.Hooks = new ReputationCombatHooks(new FactionCombatHooks(Templates), new ReputationReactionResolver(Templates, Factions, service.For));
        Assert.False(map.Combat.Hooks.CanAttack(a, b)); // an unflagged enemy player
        b.UnitFlags |= UnitFlags.Pvp;
        Assert.True(map.Combat.Hooks.CanAttack(a, b));
    }

    [Fact]
    public void Duel_MakesTheOpponentsHostile_BeforeAnyOtherRule()
    {
        (WorldRuntime world, Map map, _, Player a, Player b, _, _) = DuelTestKit.TwoPlayers();
        using WorldRuntime w = world;
        var service = new ReputationService(Factions, roll: () => 0.0);
        var resolver = new ReputationReactionResolver(Templates, Factions, service.For);
        var hooks = new ReputationCombatHooks(new FactionCombatHooks(Templates), resolver);
        Assert.False(hooks.IsHostileTo(a, b));
        DuelTestKit.Link(a, b, startTime: 100);
        Assert.True(hooks.IsHostileTo(a, b));
        Assert.True(hooks.IsHostileTo(b, a));
        Assert.True(resolver.TryGetReaction(a, b, out ReputationRank rank));
        Assert.Equal(ReputationRank.Hostile, rank);
    }

    [Fact]
    public void SameRaid_IsFriendly_AndAPetFollowsItsOwner()
    {
        (WorldRuntime world, Map map, _, Player a, Player b, _, _) = DuelTestKit.TwoPlayers(Race.Human, Race.Orc);
        using WorldRuntime w = world;
        var service = new ReputationService(Factions, roll: () => 0.0);
        var apart = new ReputationReactionResolver(Templates, Factions, service.For);
        var together = new ReputationReactionResolver(Templates, Factions, service.For, (x, y) => true);
        Assert.True(apart.TryGetReaction(a, b, out ReputationRank rank));
        Assert.NotEqual(ReputationRank.Friendly, rank);
        Assert.True(together.TryGetReaction(a, b, out rank));
        Assert.Equal(ReputationRank.Friendly, rank);

        var pet = new OwnedUnit { Owner = a, FactionTemplate = PlayerTemplate.Id };
        pet.UnitFlags |= UnitFlags.PlayerControlled;
        pet.Spawn(map, 11, 10);
        Assert.True(apart.TryGetReaction(pet, a, out rank));
        Assert.Equal(ReputationRank.Friendly, rank); // always friendly to the owner (same controlling player)
        Assert.True(apart.TryGetReaction(a, pet, out rank));
        Assert.Equal(ReputationRank.Friendly, rank);
    }

    [Fact]
    public void APetOfAHatedPlayer_IsAttackedByTheGuards_ThroughItsOwner()
    {
        using var rig = new Rig();
        rig.Service.SetReputation(rig.Player, Stormwind, ReputationMath.Bottom);
        var pet = new OwnedUnit { Owner = rig.Player, FactionTemplate = PlayerTemplate.Id };
        pet.UnitFlags |= UnitFlags.PlayerControlled;
        pet.Spawn(rig.Map, 1, 0);
        CombatTestUnit guard = rig.Npc(StormwindGuard);
        Assert.True(rig.Hooks.IsHostileTo(guard, pet));
        Assert.True(rig.Hooks.CanAttack(guard, pet));
    }
}

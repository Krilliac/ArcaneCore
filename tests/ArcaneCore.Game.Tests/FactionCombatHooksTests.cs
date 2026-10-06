using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Faction-aware production combat hooks (docs/integration/combat.md, Faction hooks).</summary>
public sealed class FactionCombatHooksTests
{
    // Human player template is 1 (TestWorld.CreatePlayer); the rows are synthetic, not DBC data.
    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),         // player race
        new FactionTemplateRecord(11, 11, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),       // friendly town NPC
        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 2),       // hostile monster
        new FactionTemplateRecord(188, 188, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0),     // neutral
        new FactionTemplateRecord(35, 0, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0, Friend1: 1), // explicit friend of player faction
        new FactionTemplateRecord(50, 50, 0, OwnMask: 8, FriendlyMask: 2, HostileMask: 2),       // both friendly and hostile bits toward the player race
        new FactionTemplateRecord(60, 60, 0, OwnMask: 2, FriendlyMask: 4, HostileMask: 0),       // player-like, friendly to mask 4
        new FactionTemplateRecord(62, 62, 0, OwnMask: 4, FriendlyMask: 0, HostileMask: 0, Enemy1: 60), // lists faction 60 as an explicit enemy
    ]);

    private static (WorldRuntime World, Map Map, Player Player, CombatTestUnit Npc) Setup(uint npcTemplate)
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        Player player = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        var npc = new CombatTestUnit { FactionTemplate = npcTemplate };
        npc.Spawn(map, 3, 0);
        return (world, map, player, npc);
    }

    [Fact]
    public void FriendlyNpc_CannotBeAttacked_AndIsReportedFriendlyToSpellTargeting()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(11);
        using WorldRuntime w = world;
        Assert.True(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.True(map.Combat.Hooks.IsFriendly(npc, player));
        Assert.False(map.Combat.Hooks.CanAttack(player, npc));
    }

    [Fact]
    public void ExplicitFriendList_MakesNpcFriendly()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(35);
        using WorldRuntime w = world;
        Assert.True(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.False(map.Combat.Hooks.CanAttack(player, npc));
    }

    [Fact]
    public void HostileAndNeutralNpcs_RemainAttackable()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(14);
        using WorldRuntime w = world;
        Assert.False(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.True(map.Combat.Hooks.CanAttack(player, npc));
        npc.FactionTemplate = 188;
        Assert.False(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.False(map.Combat.Hooks.IsFriendly(npc, player));
        Assert.True(map.Combat.Hooks.CanAttack(player, npc));
    }

    [Fact]
    public void UnknownTemplate_KeepsTheBasePermissiveRule()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(999);
        using WorldRuntime w = world;
        Assert.False(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.True(map.Combat.Hooks.IsFriendly(npc, npc));
        Assert.True(map.Combat.Hooks.CanAttack(player, npc));
        npc.FactionTemplate = 0;
        Assert.False(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.True(map.Combat.Hooks.CanAttack(player, npc));
    }

    [Fact]
    public void FriendlyNpc_StillRefusesEveryNonFactionRuleOfTheBase()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(14);
        using WorldRuntime w = world;
        npc.IsInEvadeMode = true;
        Assert.False(map.Combat.Hooks.CanAttack(player, npc));
        npc.IsInEvadeMode = false;
        npc.UnitFlags |= UnitFlags.NotAttackable1;
        Assert.False(map.Combat.Hooks.CanAttack(player, npc));
    }

    [Fact]
    public void AgreesWithFactionCreatureHostility_ForTheSameTemplatePairs()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(14);
        using WorldRuntime w = world;
        var hostility = new ArcaneCore.Game.Creatures.FactionCreatureHostility(Catalog);
        foreach (uint template in new uint[] { 11, 14, 188, 35, 999, 0 })
        {
            var creature = new ArcaneCore.Game.Creatures.Creature(
                template + 100, CreatureTestSupport.Template(template + 100, t => t.Faction = template), null,
                ArcaneCore.Kernel.WorldData.Creatures.CreatureContent.Empty, new Random(1));
            npc.FactionTemplate = template;
            bool hostile = hostility.IsHostile(creature, player);
            bool attackable = map.Combat.Hooks.CanAttack(player, npc);
            bool friendly = Catalog.Find(template) is { } t2 && Catalog.Find(player.FactionTemplate) is { } p && t2.IsFriendlyTo(p);
            if (hostile) { Assert.True(attackable, $"hostile template {template} must be attackable"); }
            if (friendly) { Assert.False(hostile, $"friendly template {template} must not be hostile"); Assert.False(attackable); }
            Assert.Equal(!friendly, attackable);
        }
    }

    // vmangos Object.cpp GetFactionReactionTo 3734-3741: IsHostileTo is tested before IsFriendlyTo, so a
    // template with both bits set toward the target is HOSTILE, and a hostile reaction does not deny the attack.
    [Fact]
    public void HostileIsEvaluatedBeforeFriendly_WhenBothBitsAreSet()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(50);
        using WorldRuntime w = world;
        Assert.True(Catalog.Find(50)!.IsHostileTo(Catalog.Find(1)!));
        Assert.True(Catalog.Find(50)!.IsFriendlyTo(Catalog.Find(1)!));
        Assert.False(map.Combat.Hooks.IsFriendly(player, npc));
        Assert.True(map.Combat.Hooks.CanAttack(player, npc));
        Assert.True(map.Combat.Hooks.CanAttack(npc, player));
    }

    // vmangos IsValidAttackTarget 3767-3769: friendly in EITHER direction denies. Template 60 is friendly to 62 by mask
    // while 62 lists 60 as an explicit enemy (62 is hostile to 60 but 60 -> 62 is Friendly).
    [Fact]
    public void FriendlyInEitherDirection_DeniesTheAttack()
    {
        (WorldRuntime world, Map map, Player player, CombatTestUnit npc) = Setup(62);
        using WorldRuntime w = world;
        player.FactionTemplate = 60;
        Assert.False(Catalog.Find(62)!.IsFriendlyTo(Catalog.Find(60)!));
        Assert.True(Catalog.Find(60)!.IsFriendlyTo(Catalog.Find(62)!));
        Assert.True(map.Combat.Hooks.IsFriendly(player, npc));
        // The reverse source is explicitly hostile to template 60, so hostile-first precedence makes it unfriendly.
        Assert.False(map.Combat.Hooks.IsFriendly(npc, player));
        Assert.False(map.Combat.Hooks.CanAttack(player, npc));
        Assert.False(map.Combat.Hooks.CanAttack(npc, player));
    }

    private static (WorldRuntime World, Map Map, CombatTestUnit A, CombatTestUnit B) SetupPair(uint a, uint b)
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        var ua = new CombatTestUnit { FactionTemplate = a };
        ua.Spawn(map, 3, 0);
        var ub = new CombatTestUnit { FactionTemplate = b };
        ub.Spawn(map, 4, 0);
        return (world, map, ua, ub);
    }

    // vmangos IsValidAttackTarget 3760-3763: neither player-controlled -> attackable only when the reaction is
    // HOSTILE in either direction; neutral and friendly are not attackable.
    [Theory]
    [InlineData(14u, 11u, true)]   // 14 is hostile to 11
    [InlineData(11u, 14u, true)]   // other direction: 14 -> 11 is hostile
    [InlineData(14u, 14u, false)]  // same faction: friendly
    [InlineData(14u, 188u, false)] // neutral
    [InlineData(14u, 999u, false)] // template missing from the catalog: NEUTRAL (Object.cpp 3705-3709)
    [InlineData(50u, 1u, true)]    // both bits set: hostile wins
    public void CreatureVsCreature_NeedsAHostileReactionEitherWay(uint a, uint b, bool attackable)
    {
        (WorldRuntime world, Map map, CombatTestUnit ua, CombatTestUnit ub) = SetupPair(a, b);
        using WorldRuntime w = world;
        Assert.Equal(attackable, map.Combat.Hooks.CanAttack(ua, ub));
        Assert.Equal(attackable, map.Combat.Hooks.CanAttack(ub, ua));
    }

    // UNIT_FLAG_PLAYER_CONTROLLED (UnitDefines.h:494, pets/charms/totems) leaves the CvC branch for the PvC/CvP branch:
    // only friendliness denies, neutral stays attackable.
    [Fact]
    public void PlayerControlledUnit_UsesThePvcBranch_NotTheCvcBranch()
    {
        (WorldRuntime world, Map map, CombatTestUnit pet, CombatTestUnit other) = SetupPair(14, 188);
        using WorldRuntime w = world;
        Assert.False(map.Combat.Hooks.CanAttack(pet, other)); // CvC neutral
        pet.UnitFlags |= UnitFlags.PlayerControlled;
        Assert.True(map.Combat.Hooks.CanAttack(pet, other));
        Assert.True(map.Combat.Hooks.CanAttack(other, pet));
        other.FactionTemplate = 14; // friendly to the pet
        Assert.False(map.Combat.Hooks.CanAttack(pet, other));
    }

    [Fact]
    public void PlayerVsPlayer_IsUnchanged()
    {
        (WorldRuntime world, Map map, Player human, _) = Setup(14);
        using WorldRuntime w = world;
        Player dwarf = CombatTestKit.AddPlayer(world, 3, 2, 2, new FakeSession(3), Race.Dwarf);
        Player orc = CombatTestKit.AddPlayer(world, 2, 2, 0, new FakeSession(2), Race.Orc);
        Assert.True(map.Combat.Hooks.IsFriendly(human, dwarf));
        Assert.False(map.Combat.Hooks.CanAttack(human, orc)); // unflagged enemy player
        orc.UnitFlags |= UnitFlags.Pvp;
        Assert.True(map.Combat.Hooks.CanAttack(human, orc));
    }

    [Fact]
    public void TryRegister_FirstWins_AndRefusesASecondRegistration()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var first = new FactionCombatHooks(Catalog);
        var second = new FactionCombatHooks(Catalog);
        Assert.Same(CombatHooks.Default, CombatHooks.For(world));
        Assert.True(CombatHooks.TryRegister(world, first));
        Assert.False(CombatHooks.TryRegister(world, second));
        Assert.Same(first, CombatHooks.For(world));
        Assert.Throws<ArgumentNullException>(() => CombatHooks.TryRegister(world, null!));
    }

    [Fact]
    public void TryRegister_RefusesTheDefaultInstance()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Assert.False(CombatHooks.TryRegister(world, CombatHooks.Default));
        Assert.Same(CombatHooks.Default, CombatHooks.For(world));
    }
}

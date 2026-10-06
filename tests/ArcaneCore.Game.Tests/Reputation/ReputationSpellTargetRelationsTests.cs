using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using ArcaneCore.Kernel.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

public sealed class ReputationSpellTargetRelationsTests
{
    [Fact]
    public void KnownReputationNpc_OverridesTemplateAndTracksAtWar()
    {
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        player.FactionTemplate = PlayerTemplate.Id;
        var reputation = new ReputationService(Factions);
        reputation.Track(player, Human());
        Creature npc = Npc(BootyBayNpc);
        var relations = new ReputationSpellTargetRelations(() => Templates, () => reputation, CombatHookRelations.Instance);

        Assert.True(relations.CanAssist(player, npc)); // Neutral/Friendly despite the template pair being hostile.
        Assert.True(reputation.SetReputation(player, BootyBay, -6000));
        Assert.False(relations.CanAssist(player, npc)); // Hostile.
        Assert.True(reputation.SetReputation(player, BootyBay, 3000));
        Assert.True(reputation.For(player)!.SetAtWarByClient(Get(BootyBay).ReputationListId, false));
        Assert.True(reputation.For(player)!.SetAtWarByClient(Get(BootyBay).ReputationListId, true));
        Assert.False(relations.CanAssist(player, npc)); // At war makes the player reaction hostile.
    }

    [Fact]
    public void MissingStateNonReputationAndOwnedCreature_KeepFallback()
    {
        Player player = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        var reputation = new ReputationService(Factions);
        reputation.Track(player, Human());
        Creature npc = Npc(DefiasNpc);
        var relations = new ReputationSpellTargetRelations(() => Templates, () => reputation, CombatHookRelations.Instance);

        Assert.True(relations.CanAssist(player, npc)); // Non-reputation faction delegates to the existing fallback.
        npc.SetOwnerGuid(player.Guid);
        Assert.True(relations.CanAssist(player, npc)); // Owned creature is outside the player/NPC adapter scope.
        Assert.True(relations.CanAssist(player, player)); // Self remains assistable.
        Assert.True(relations.CanAssist(player, TestWorld.CreatePlayer(3, 0, 0, new FakeSession()))); // Player policy remains fallback-owned.
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1000, true)]
    [InlineData(-6000, false)]
    [InlineData(-42000, false)]
    [InlineData(3000, true)]
    public void ExplicitFriendProducer_UsesLoadedRankBeforeTemplateHostility(int standing, bool accepted)
    {
        using var kit = new SpellTestKit(Spell(993100,
            Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)));
        (Player player, _) = kit.AddPlayer(1);
        player.FactionTemplate = PlayerTemplate.Id;
        player.Map!.Combat.Hooks = new FactionCombatHooks(Templates);
        Creature npc = Npc(BootyBayNpc);
        player.Map!.AddObject(npc);
        npc.Health = 50;
        var reputation = new ReputationService(Factions);
        reputation.Track(player, Human());
        reputation.SetReputation(player, BootyBay, standing);
        var relations = new ReputationSpellTargetRelations(() => Templates, () => reputation, CombatHookRelations.Instance);
        Assert.False(CombatHookRelations.Instance.CanAssist(player, npc));
        kit.System.Units = new MapUnits();
        kit.System.Relations = relations;
        kit.System.CombatRules = SpellCombatRules.Neutral;

        SpellCastResult result = kit.System.CastSpell(player, 993100, SpellCastTargets.ForUnit(npc.Guid), true);
        Assert.Equal(accepted ? SpellCastResult.CastOk : SpellCastResult.BadTargets, result);
        Assert.Equal(accepted ? 60u : 50u, npc.Health);
    }

    [Fact]
    public void ExplicitFriendProducer_AtWarTogglesWithoutChangingAttackOrAreaRelation()
    {
        using var kit = new SpellTestKit(Spell(993101,
            Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitFriend)));
        (Player player, _) = kit.AddPlayer(1);
        player.FactionTemplate = PlayerTemplate.Id;
        player.Map!.Combat.Hooks = new FactionCombatHooks(Templates);
        Creature npc = Npc(BootyBayNpc);
        player.Map!.AddObject(npc);
        npc.Health = 50;
        var reputation = new ReputationService(Factions);
        reputation.Track(player, Human());
        var relations = new ReputationSpellTargetRelations(() => Templates, () => reputation, CombatHookRelations.Instance);
        kit.System.Units = new MapUnits();
        kit.System.Relations = relations;
        kit.System.CombatRules = SpellCombatRules.Neutral;
        Assert.Equal(CombatHookRelations.Instance.IsFriendly(player, npc), relations.IsFriendly(player, npc));
        Assert.Equal(CombatHookRelations.Instance.IsHostile(player, npc), relations.IsHostile(player, npc));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 993101, SpellCastTargets.ForUnit(npc.Guid), true));
        Assert.Equal(60u, npc.Health);
        Assert.True(reputation.For(player)!.SetAtWarByClient(Get(BootyBay).ReputationListId, true));
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(player, 993101, SpellCastTargets.ForUnit(npc.Guid), true));
        Assert.Equal(60u, npc.Health);
        Assert.True(reputation.For(player)!.SetAtWarByClient(Get(BootyBay).ReputationListId, false));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 993101, SpellCastTargets.ForUnit(npc.Guid), true));
        Assert.Equal(70u, npc.Health);
    }

    [Fact]
    public void SameGuidReplacementCannotBorrowTrackedReputation()
    {
        Player oldPlayer = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        Player replacement = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        oldPlayer.FactionTemplate = replacement.FactionTemplate = PlayerTemplate.Id;
        var reputation = new ReputationService(Factions);
        reputation.Track(oldPlayer, Human());
        var relations = new ReputationSpellTargetRelations(() => Templates, () => reputation, new DenyFallback());
        Creature npc = Npc(BootyBayNpc);
        Assert.Equal(oldPlayer.Guid, replacement.Guid);
        Assert.True(relations.CanAssist(oldPlayer, npc));
        Assert.False(relations.CanAssist(replacement, npc));
        Assert.Null(reputation.For(replacement));
    }

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Map?.FindObject(guid) as Unit;
    }

    private sealed class DenyFallback : ISpellTargetRelations
    {
        public bool IsFriendly(Unit caster, Unit target) => false;
        public bool IsHostile(Unit caster, Unit target) => true;
        public bool CanAssist(Unit caster, Unit target) => false;
    }

    private static Creature Npc(FactionTemplateRecord template)
    {
        var content = CreatureTestSupport.Content([CreatureTestSupport.Template(template.Id)] , []);
        var npc = new Creature(1, content.Templates.Single(), null, content, new Random(1));
        npc.FactionTemplate = template.Id;
        npc.Health = 100;
        npc.MaxHealth = 100;
        return npc;
    }
}

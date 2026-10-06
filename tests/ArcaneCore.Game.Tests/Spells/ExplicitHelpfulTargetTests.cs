using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Duel;
using ArcaneCore.Kernel.Npc;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Explicit positive selectors reject proven hostility while preserving neutral fallback.</summary>
public sealed class ExplicitHelpfulTargetTests
{
    private const uint Helpful = 900410;
    private const uint HelpfulChain = 900411;
    private const uint GenericUnit = 900412;
    private const uint HarmfulFriend = 900413;

    private static readonly FactionTemplateCatalog Catalog = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(11, 11, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 8, HostileMask: 2),
        new FactionTemplateRecord(188, 188, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0),
        new FactionTemplateRecord(62, 62, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 0, Enemy1: 1),
    ]);

    [Fact]
    public void ExplicitHelpfulTarget_AcceptsFriendlyAndNeutralNpc_ButRejectsKnownHostileBothDirections()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Map map = caster.Map!;
        map.Combat.Hooks = new FactionCombatHooks(Catalog);

        CombatTestUnit friendly = AddNpc(kit, 11, 3);
        CombatTestUnit neutral = AddNpc(kit, 188, 4);
        CombatTestUnit hostile = AddNpc(kit, 14, 5);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(friendly.Guid), triggered: true));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, HelpfulChain, SpellCastTargets.ForUnit(neutral.Guid), triggered: true));
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(hostile.Guid), triggered: true));

        Assert.True(new FactionCombatHooks(Catalog).IsHostileTo(caster, hostile));
        Assert.True(new FactionCombatHooks(Catalog).IsHostileTo(hostile, caster));
    }

    [Fact]
    public void ExplicitHelpfulTarget_RejectsReverseOnlyFactionHostility()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Map map = caster.Map!;
        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        CombatTestUnit reverseHostile = AddNpc(kit, 62, 3);
        var hooks = new FactionCombatHooks(Catalog);

        Assert.False(hooks.IsHostileTo(caster, reverseHostile));
        Assert.True(hooks.IsHostileTo(reverseHostile, caster));
        Assert.Equal(SpellCastResult.BadTargets,
            kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(reverseHostile.Guid), triggered: true));
    }

    [Fact]
    public void ExplicitHelpfulTarget_UsesCustomRelationProviderHostility()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3);
        var relations = new FakeRelations();
        relations.Hostile.Add(target.Guid);
        kit.System.Relations = relations;

        Assert.Equal(SpellCastResult.BadTargets,
            kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(target.Guid), triggered: true));
    }

    [Fact]
    public void ExplicitHelpfulTarget_AcceptsUnknownNpcAndNoCatalogFallback()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Map map = caster.Map!;
        CombatTestUnit unknown = AddNpc(kit, 9999, 3);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(unknown.Guid), triggered: true));

        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(unknown.Guid), triggered: true));
    }

    [Fact]
    public void ExplicitHelpfulTarget_PreservesSelfAndCantTargetSelfRules()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(SpellCastResult.BadTargets, kit.System.CastSpell(caster, HelpfulChain, SpellCastTargets.ForSelf(), triggered: true));
    }

    [Fact]
    public void ExplicitHelpfulTarget_UsesExistingSameTeamPlayerRelation()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player teammate, _) = kit.AddPlayer(2, 3);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, Helpful, SpellCastTargets.ForUnit(teammate.Guid), triggered: true));
    }

    [Fact]
    public void GenericUnitSelector_RemainsUnclassifiedByHelpfulRule()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3);
        var relations = new FakeRelations();
        relations.Hostile.Add(target.Guid);
        kit.System.Relations = relations;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, GenericUnit, SpellCastTargets.ForUnit(target.Guid), triggered: true));
    }

    [Fact]
    public void ExplicitHelpfulTarget_RefusesStartedDuelOpponent()
    {
        (WorldRuntime world, _, _, Player caster, Player opponent, _, _) = DuelTestKit.TwoPlayers();
        using (world)
        {
            DuelTestKit.Link(caster, opponent, startTime: 100);
            var relations = new CombatHookRelations();

            Assert.False(relations.CanAssist(caster, opponent));
        }
    }

    [Fact]
    public void NegativeUnitFriendSelector_RemainsOnExistingValidationPath()
    {
        using var kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Map map = caster.Map!;
        map.Combat.Hooks = new FactionCombatHooks(Catalog);
        CombatTestUnit hostile = AddNpc(kit, 14, 3);

        Assert.False(kit.Store.Get(HarmfulFriend)!.IsPositive);
        Assert.Equal(SpellCastResult.CastOk,
            kit.System.CastSpell(caster, HarmfulFriend, SpellCastTargets.ForUnit(hostile.Guid), triggered: true));
    }

    private static SpellTestKit NewKit()
    {
        var kit = new SpellTestKit(
            Spell(Helpful, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitFriend)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(HelpfulChain, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.UnitFriendChainHeal)) with
            {
                AttributesEx = SpellAttributesEx.CantTargetSelf,
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(GenericUnit, Effect(SpellEffectName.Heal, 20, SpellImplicitTarget.Unit)) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(HarmfulFriend, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.ModStun)) with
            {
                Attributes = SpellAttributes.AuraIsDebuff,
                Duration = new SpellDuration(1000, 0, 1000),
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        kit.System.Relations = new CombatHookRelations();
        kit.System.Units = new AllMapUnits();
        return kit;
    }

    private sealed class AllMapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private static CombatTestUnit AddNpc(SpellTestKit kit, uint factionTemplate, float x)
    {
        var npc = new CombatTestUnit { FactionTemplate = factionTemplate, MaxHealth = 100, Health = 10 };
        npc.Relocate(x, 0, kit.World.GetMap(0).Players.First().Z, 0, 0);
        kit.World.GetMap(0).AddObject(npc);
        npc.Map!.Combat.Track(npc);
        kit.World.RunTick(0);
        return npc;
    }
}

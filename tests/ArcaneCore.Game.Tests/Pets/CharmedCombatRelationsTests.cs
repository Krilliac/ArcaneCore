using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// A player mind controlled by an enemy (a real charm aura through <see cref="CharmService"/>) is hostile to the dispeller's side
/// whatever its race team, so the dispel takes the charm first (vmangos Spell::EffectDispel priorityDispel, SpellEffects.cpp:2492-2499,
/// reading Unit::IsFriendlyTo, Object.cpp:3614-3663).
/// </summary>
public sealed class CharmedCombatRelationsTests
{
    private const uint Charm = 933001;
    private const uint MagicBuff = 933002;
    private const uint DispelMagic = 933003;
    private const uint AllyTemplate = 1;
    private const uint EnemyTemplate = 2;

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    private static SpellInfo Timed(SpellInfo spell) => spell with
    {
        Dispel = 1, Duration = new SpellDuration(30000, 0, 30000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    private static SpellTestKit Scene(out Player dispeller, out Player thrall, out Player enemy)
    {
        var kit = new SpellTestKit(
            Timed(SpellTestKit.Spell(Charm, SpellTestKit.Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.Unit, AuraType.ModCharm))),
            Timed(SpellTestKit.Spell(MagicBuff, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))),
            SpellTestKit.Spell(DispelMagic, SpellTestKit.Effect(SpellEffectName.Dispel, 1, SpellImplicitTarget.Unit, misc: 1)) with
            {
                StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
        kit.System.Units = new MapUnits();
        kit.System.Relations = CombatHookRelations.Instance;
        new CharmService().Install(kit.System);
        (dispeller, _) = kit.AddPlayer(1, 5, 5);
        (thrall, _) = kit.AddPlayer(2, 7, 5);
        (enemy, _) = kit.AddPlayer(3, 9, 5, race: Race.Orc);
        enemy.FactionTemplate = EnemyTemplate;
        var catalog = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(AllyTemplate, AllyTemplate, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 4),
            new FactionTemplateRecord(EnemyTemplate, EnemyTemplate, 0, OwnMask: 4, FriendlyMask: 4, HostileMask: 2),
        ]);
        dispeller.Map!.Combat.Hooks = new FactionCombatHooks(catalog);
        return kit;
    }

    [Fact]
    public void MindControlledSameTeamPlayer_IsHostileToTheDispellersSide()
    {
        using SpellTestKit kit = Scene(out Player dispeller, out Player thrall, out Player enemy);

        Assert.True(CombatHookRelations.Instance.IsFriendly(dispeller, thrall));
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(enemy, Charm, SpellCastTargets.ForUnit(thrall.Guid), triggered: true));
        Assert.Equal(enemy.Guid, thrall.CharmerGuid);
        Assert.Equal(EnemyTemplate, thrall.FactionTemplate);

        Assert.False(CombatHookRelations.Instance.IsFriendly(dispeller, thrall));
        Assert.True(CombatHookRelations.Instance.IsFriendly(enemy, thrall));
        Assert.False(CombatHookRelations.Instance.CanAssist(dispeller, thrall));
    }

    [Fact]
    public void PriorityDispel_OnAMindControlledAlly_RemovesTheCharmFirst()
    {
        using SpellTestKit kit = Scene(out Player dispeller, out Player thrall, out Player enemy);
        kit.System.CastSpell(thrall, MagicBuff, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(enemy, Charm, SpellCastTargets.ForUnit(thrall.Guid), triggered: true));

        List<(SpellAuraHolder Holder, int Stacks)> candidates = kit.System.DispelCandidates(dispeller, thrall, null, 1, out int priority);
        Assert.Equal(2, candidates.Count);
        Assert.True(priority >= 0, "the charm on a mind-controlled ally was not the priority dispel candidate");
        Assert.Equal(Charm, candidates[priority].Holder.Spell.Id);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(dispeller, DispelMagic, SpellCastTargets.ForUnit(thrall.Guid), triggered: true));

        Assert.True(thrall.CharmerGuid.IsEmpty);
        Assert.Equal(AllyTemplate, thrall.FactionTemplate);
        Assert.True(CombatHookRelations.Instance.IsFriendly(dispeller, thrall));
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// vmangos Spell::EffectDispel priorityDispel (SpellEffects.cpp:2492-2499): the charm info of a charmed summon lives in
/// <c>Creature.Summon.Charm</c> (Unit::InitCharmInfo returns the pet's own), not in the unit control state, so the original-faction
/// priority must read it from there too.
/// </summary>
public sealed class DispelCharmedSummonTests
{
    private const uint Charm = 932002;
    private const uint MagicBuff = 932003;
    private const uint CasterTemplate = 9_002;
    private const uint CreatureFaction = 9_003;

    private sealed class MapUnits : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    [Fact]
    public void CharmedPetSummon_WhoseTemplateFactionIsFriendly_IsTheFirstDispelPick()
    {
        using var kit = new SpellTestKit(
            SpellTestKit.Spell(Charm, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.Unit, AuraType.ModCharm)) with
            {
                Dispel = 1, Duration = new SpellDuration(30000, 0, 30000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            SpellTestKit.Spell(MagicBuff, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
            {
                Dispel = 1, Duration = new SpellDuration(30000, 0, 30000), SpellVisual = 1, StartRecoveryCategory = 0, StartRecoveryTime = 0,
            });
        kit.System.Units = new MapUnits();
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        caster.FactionTemplate = CasterTemplate;

        var template = new CreatureTemplate
        {
            Entry = 932100, Name = "Charmed summon", MinLevel = 20, MaxLevel = 20,
            MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [1], Faction = CreatureFaction,
        };
        var creature = new Creature(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        Map map = kit.World.GetMap(0);
        creature.Relocate(caster.X + 3, caster.Y, caster.Z, 0, 0);
        creature.MapId = 0;
        map.AddObject(creature);
        kit.World.RunTick(0);
        creature.Level = 20;
        creature.MaxHealth = creature.Health = 100;
        creature.Summon = new SummonLinks(SummonKind.Pet, caster.Guid, 1, TotemSlots.None, 0) { Charm = new CharmInfo(ReactState.Passive) };
        relations.Hostile.Add(creature.Guid);

        // Hostile by its current template; its own template faction is friendly to the caster's.
        var catalog = new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(CasterTemplate, CasterTemplate, 0, OwnMask: 1, FriendlyMask: 0, HostileMask: 0),
            new FactionTemplateRecord(CreatureFaction, CreatureFaction, 0, OwnMask: 2, FriendlyMask: 1, HostileMask: 0),
        ]);
        caster.Map!.Combat.Hooks = new FactionCombatHooks(catalog);

        kit.System.CastSpell(creature, MagicBuff, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(caster, Charm, SpellCastTargets.ForUnit(creature.Guid), triggered: true);

        List<(SpellAuraHolder Holder, int Stacks)> candidates = kit.System.DispelCandidates(caster, creature, null, 1, out int priority);

        Assert.Equal(2, candidates.Count);
        Assert.True(priority >= 0, "no priority charm candidate for a charmed summon");
        Assert.Equal(Charm, candidates[priority].Holder.Spell.Id);
    }
}

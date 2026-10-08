using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets;

// SPELL_EFFECT_SUMMON_DEMON (112): Curse of Doom's Doomguard (18662), the Ritual of Doom and Inferno summons.
public sealed partial class SummonService
{
    /// <summary>The longest a demon stays when its spell has no duration (vmangos <c>m_duration &gt; 0 ? m_duration : 3600000</c>).</summary>
    public const int SummonDemonDefaultDurationMs = 3_600_000;

    /// <summary>
    /// vmangos Spell::EffectSummonDemon (SpellEffects.cpp:5796-5819): a temporary summon of the effect's creature entry at the spell's destination
    /// (the caster for TARGET_LOCATION_CASTER_DEST, Spell.cpp:3166-3172), facing as the caster faces, with the template's faction and the caster's
    /// level ("might not always work correctly, maybe the creature that dies from CoD casts the effect on itself"), despawning after the spell
    /// duration (an hour without one) or at its death.
    /// <para>
    /// LIMITS: vmangos summons with TEMPSUMMON_TIMED_COMBAT_OR_DEAD_DESPAWN; the summon here is a <see cref="SummonKind.Wild"/> summon, which is
    /// killed when the timer runs out out of combat (TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN) instead of despawning, and a summoning ritual game
    /// object as the destination (Ritual of Doom) and the spell script's <c>OnSummon</c> (Inferno's Enslave Demon) are not modelled.
    /// </para>
    /// </summary>
    private void EffectSummonDemon(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        if (!ReferenceEquals(context.Target, caster))
        {
            // vmangos runs the effect once, on the caster (Spell.cpp:3166-3172); area selectors list other units too.
            return;
        }

        SpellInfo spell = context.Spell;
        uint entry = (uint)context.Effect.MiscValue;
        if (entry == 0 || !TryGetSystems(caster, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"demon-template:{entry}", "creature entry {Entry} not found for spell {Spell} demon summon", entry, spell.Id);
            return;
        }

        SpellCastTargets targets = context.Cast.Targets;
        (float x, float y, float z) = targets.HasDest ? targets.Dest : (caster.X, caster.Y, caster.Z);
        int duration = context.Cast.Duration > 0 ? context.Cast.Duration : SummonDemonDefaultDurationMs;
        float orientation = caster.Orientation;
        Creature summon = creatures.SpawnSummoned(template, HighGuid.Unit, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Wild, caster.Guid, spell.Id, TotemSlots.None, duration);
            creature.SetUInt32(UpdateFields.UnitCreatedBySpell, spell.Id);
            creature.Level = caster.Level;
            return new CreatureHome(x, y, z, orientation);
        });

        pets.Options = _options;
        pets.Register(summon, this);
    }
}

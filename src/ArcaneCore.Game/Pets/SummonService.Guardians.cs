using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Pets;

// Wild summons, guardians and mini pets (SPELL_EFFECT_SUMMON_WILD 41, SUMMON_GUARDIAN 42, SUMMON_CRITTER 97).
public sealed partial class SummonService
{
    // --- SPELL_EFFECT_SUMMON_GUARDIAN ---------------------------------------------------------------

    /// <summary>
    /// vmangos Spell::EffectSummonGuardian (SpellEffects.cpp:2775-2914): a direct (not triggered)
    /// second cast by a player dismisses its guardians of the entry (and stops there unless the
    /// spell has both a duration and a category, i.e. a cooldown); a non-player caster stops at
    /// <see cref="PetOptions.MaxNpcGuardiansPerEntry"/> guardians of the entry; the level is the
    /// template's range, or for a non-player caster with a non-positive EffectMultipleValue its own
    /// level plus that value; <c>damage</c> guardians are made (at least one), the first at the
    /// destination (facing <c>-orientation</c>), the others on a random point within the effect
    /// radius of it (facing <c>+orientation</c>), or all at the caster when the spell has no
    /// destination. Each follows at <c>pi/2 + pi/6 * (guardians + pet)</c>.
    /// </summary>
    private void EffectSummonGuardian(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        SpellInfo spell = context.Spell;
        uint entry = (uint)context.Effect.MiscValue;
        if (entry == 0 || !TryGetSystems(caster, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"guardian-template:{entry}", "creature entry {Entry} not found for spell {Spell} guardian summon", entry, spell.Id);
            return;
        }

        // second direct cast unsummon guardian(s) (guardians without like functionality have cooldown > spawn time)
        if (!context.Cast.IsTriggered && caster is Player)
        {
            bool found = false;
            foreach (Creature old in pets.GuardiansOf(caster, entry).ToArray())
            {
                Unsummon(old);
                found = true;
            }

            if (found && !(spell.Duration != default && spell.Category != 0))
            {
                return;
            }
        }

        // Hard cap for NPC summoned guardians
        if (caster is not Player && pets.GuardiansOf(caster, entry).Count() > _options.MaxNpcGuardiansPerEntry)
        {
            return;
        }

        // Guardian pets use their creature template level by default
        int level = RandomLevel(template);
        if (caster is not Player && context.Effect.MultipleValue <= 0)
        {
            // If EffectMultipleValue <= 0, guardian pets use their caster level modified by EffectMultipleValue for their own level
            uint result = (uint)Math.Max(caster.Level + context.Effect.MultipleValue, 0f);
            if (result > 0 && result <= PetConstants.MaxCreatureLevel)
            {
                level = (int)result;
            }
        }

        SpellCastTargets targets = context.Cast.Targets;
        (float centerX, float centerY, float centerZ) = targets.HasDest ? targets.Dest : (caster.X, caster.Y, caster.Z);
        int amount = context.Value > 0 ? context.Value : 1;
        for (int count = 0; count < amount; count++)
        {
            // Summon 1 unit in dest location, the others at random points around it; without a destination near the caster.
            float x = caster.X;
            float y = caster.Y;
            float z = caster.Z;
            float orientation = caster.Orientation;
            if (targets.HasDest)
            {
                if (count == 0)
                {
                    (x, y, z) = (centerX, centerY, centerZ);
                    orientation = Creature.NormalizeOrientation(-caster.Orientation);
                }
                else
                {
                    (x, y, z) = RandomPoint(creatures, centerX, centerY, centerZ, context.Effect.Radius);
                }
            }

            int guardians = pets.GuardiansOf(caster).Count() + (caster.PetGuid.IsEmpty ? 0 : 1);
            float followAngle = PetConstants.FollowAngle;
            if (guardians != 0)
            {
                followAngle += MathF.PI / 6 * guardians;
                while (followAngle > MathF.PI * 2)
                {
                    followAngle -= MathF.PI * 2;
                }
            }

            (float spawnX, float spawnY, float spawnZ, float spawnO) = (x, y, z, orientation);
            int spellDuration = spell.GetDuration();
            uint petNumber = NextPetNumber();
            Creature guardian = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
            {
                creature.Summon = new SummonLinks(SummonKind.Guardian, caster.Guid, spell.Id, TotemSlots.None, spellDuration, followAngle);
                ApplyOwner(creature, caster, spell.Id);
                InitPet(creature, SummonKind.Guardian, caster, petNumber);
                creature.Level = (byte)level; // InitStatsForLevel(level, owner): the stats are the stats lane's
                PetInitializer.CopyOwnerControlFlags(creature, caster); // ... and its owner-flag tail
                return new CreatureHome(spawnX, spawnY, spawnZ, spawnO);
            }, petNumber);

            pets.Options = _options;
            pets.Register(guardian, this);
            AttachPetAi(guardian);
        }
    }

    // --- SPELL_EFFECT_SUMMON_WILD ---------------------------------------------------------------------

    /// <summary>
    /// vmangos Spell::EffectSummonWild (SpellEffects.cpp:2685-2773): <c>damage</c> temporary
    /// summons of the entry (at least one), the first at the destination, the others at random
    /// points within the effect radius of it; without a destination at the radius in front of the
    /// caster, or at the caster when the radius is 0. They keep their template faction and level,
    /// carry only <c>UNIT_CREATED_BY_SPELL</c> (the creator field is not set for these spells) and
    /// despawn after the spell duration, or at death when it has none.
    /// </summary>
    private void EffectSummonWild(SpellEffectContext context)
    {
        Unit caster = context.Caster;
        SpellInfo spell = context.Spell;
        uint entry = (uint)context.Effect.MiscValue;
        if (entry == 0 || !TryGetSystems(caster, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"wild-template:{entry}", "creature entry {Entry} not found for spell {Spell} wild summon", entry, spell.Id);
            return;
        }

        SpellCastTargets targets = context.Cast.Targets;
        (float centerX, float centerY, float centerZ) = targets.HasDest ? targets.Dest : (caster.X, caster.Y, caster.Z);
        float radius = context.Effect.Radius;
        int duration = spell.GetDuration();
        int amount = context.Value > 0 ? context.Value : 1;
        for (int count = 0; count < amount; count++)
        {
            float x;
            float y;
            float z;
            if (targets.HasDest)
            {
                if (count == 0)
                {
                    (x, y, z) = (centerX, centerY, centerZ);
                }
                else
                {
                    (x, y, z) = RandomPoint(creatures, centerX, centerY, centerZ, radius);
                }
            }
            else if (radius > 0.0f)
            {
                // not using bounding radius of caster here
                (x, y) = ClosePoint(caster, 0.0f, radius, caster.Orientation);
                z = creatures.GroundZ(x, y, caster.Z) ?? caster.Z;
            }
            else
            {
                (x, y, z) = (caster.X, caster.Y, caster.Z);
            }

            (float spawnX, float spawnY, float spawnZ) = (x, y, z);
            Creature summon = creatures.SpawnSummoned(template, HighGuid.Unit, creature =>
            {
                creature.Summon = new SummonLinks(SummonKind.Wild, caster.Guid, spell.Id, TotemSlots.None, duration);
                creature.SetUInt32(UpdateFields.UnitCreatedBySpell, spell.Id);
                return new CreatureHome(spawnX, spawnY, spawnZ, caster.Orientation);
            });

            pets.Options = _options;
            pets.Register(summon, this);
        }
    }

    // --- SPELL_EFFECT_SUMMON_CRITTER ------------------------------------------------------------------

    /// <summary>
    /// vmangos Spell::EffectSummonCritter (SpellEffects.cpp:5400-5472): players only; a second cast
    /// of the same entry just dismisses the mini pet, another entry replaces it. The mini pet
    /// appears at the caster (a spell with a destination puts it <see cref="PetConstants.FollowDistance"/>
    /// away at <see cref="PetConstants.MiniPetSummonAngle"/>, as vmangos does), owned by the player
    /// but not its <c>UNIT_FIELD_SUMMON</c> pet, keeps its template level and NPC flags (some
    /// mini pets have quests), and faces the player.
    /// </summary>
    private void EffectSummonCritter(SpellEffectContext context)
    {
        if (context.Caster is not Player player)
        {
            return;
        }

        SpellInfo spell = context.Spell;
        uint entry = (uint)context.Effect.MiscValue;
        if (entry == 0 || !TryGetSystems(player, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"critter-template:{entry}", "creature entry {Entry} not found for spell {Spell} critter summon", entry, spell.Id);
            return;
        }

        Creature? old = pets.MiniPetOf(player);

        // for same pet just despawn
        if (old is not null && old.Entry == entry)
        {
            Unsummon(old);
            return;
        }

        // despawn old pet before summon new
        if (old is not null)
        {
            Unsummon(old);
        }

        bool hasDest = context.Cast.Targets.HasDest;
        int duration = spell.GetDuration();
        uint petNumber = NextPetNumber();
        Creature critter = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.MiniPet, player.Guid, spell.Id, TotemSlots.None, duration, PetConstants.MiniPetFollowAngle);
            ApplyOwner(creature, player, spell.Id);
            InitPet(creature, SummonKind.MiniPet, player, petNumber);

            float x = player.X;
            float y = player.Y;
            float z = player.Z;
            if (hasDest)
            {
                (x, y) = ClosePoint(player, creature.BoundingRadius, PetConstants.FollowDistance, player.Orientation + PetConstants.MiniPetSummonAngle);
                z = creatures.GroundZ(x, y, player.Z) ?? player.Z;
            }

            // SetFacingToObject(player)
            float facing = Creature.NormalizeOrientation(MathF.Atan2(player.Y - y, player.X - x));
            return new CreatureHome(x, y, z, facing);
        }, petNumber);

        pets.Options = _options;
        pets.Register(critter, this);
        AttachPetAi(critter);
        PetInitializer.InitCreateSpells(critter, Content, _spells); // "e.g. disgusting oozeling has a create spell as critter" (SpellEffects.cpp:5452)
    }

    // --- helpers --------------------------------------------------------------------------------------

    /// <summary>vmangos <c>urand(level_min, level_max)</c> over the template's level range.</summary>
    private int RandomLevel(CreatureTemplate template)
    {
        int min = Math.Min(template.MinLevel, template.MaxLevel);
        int max = Math.Max(template.MinLevel, template.MaxLevel);
        return Math.Max(min == max ? min : _random.Next(min, max + 1), 1);
    }

    /// <summary>
    /// vmangos WorldObject::GetRandomPoint (Object.cpp:1991-2063): below 0.1 yd the centre itself.
    /// Without a navmesh walk query the point is uniform over the disc at the terrain height (or the
    /// centre's Z), as the creature wander does (docs/integration/pets.md).
    /// </summary>
    private (float X, float Y, float Z) RandomPoint(CreatureMapSystem creatures, float centerX, float centerY, float centerZ, float distance)
    {
        if (distance < 0.1f)
        {
            return (centerX, centerY, centerZ);
        }

        double angle = _random.NextDouble() * 2 * Math.PI;
        double range = distance * Math.Sqrt(_random.NextDouble());
        float x = centerX + (float)(range * Math.Cos(angle));
        float y = centerY + (float)(range * Math.Sin(angle));
        return (x, y, creatures.GroundZ(x, y, centerZ) ?? centerZ);
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The spell a totem casts on its own (vmangos <c>creature_template.totem_spell_id</c>, Totem.cpp:220-223).
/// The creature content of this build has no such column yet (classic-db does not carry it either;
/// cmangos reads the creature spell list instead), so the world supplies this seam; without it a
/// totem is visual only and the service says so once.
/// </summary>
public interface ITotemSpellSource
{
    /// <summary>The spell id of <paramref name="template"/>'s totem, 0 for a totem that only exists for its model.</summary>
    uint GetTotemSpell(CreatureTemplate template);
}

/// <summary>
/// Summoning (vmangos Spell::EffectSummonTotem, EffectSummon): the production
/// <see cref="ISpellSummonSink"/> and the owner of the totem effects. One instance serves every
/// map; the per-map state (totem slots, timers) lives in <see cref="PetMapSystem"/> and the
/// creature itself in the map's <see cref="CreatureMapSystem"/>.
/// <para>
/// Install it on a <see cref="SpellSystem"/> with <see cref="Install"/>: the totem effects
/// (SUMMON_TOTEM and the four slot effects) are registered, and SPELL_EFFECT_SUMMON keeps its
/// built-in handler, which now reaches this service through the sink, so the quest reward
/// preflight (<see cref="CanSummon"/>) models exactly what a cast does.
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class SummonService : ISpellSummonSink
{
    /// <summary>vmangos Spell::EffectSummonTotem: <c>CreatureCreatePos(caster, orientation, 2.0f, angle)</c>.</summary>
    public const float TotemDistance = 2.0f;

    /// <summary>vmangos Totem::Create: a totem more than this far in Z from its owner is put at the owner's Z.</summary>
    public const float TotemMaxZDifference = 5.0f;

    private readonly PetOptions _options;
    private readonly Func<Map, CreatureMapSystem?> _systems;
    private readonly ILogger _logger;
    private readonly HashSet<string> _warned = [];
    private SpellSystem? _spells;

    /// <param name="options">Tuning (retail defaults when null); handed to every map's <see cref="PetMapSystem"/>.</param>
    /// <param name="systems">The creature system a summon spawns into; the default is the one attached to the map.</param>
    public SummonService(PetOptions? options = null, Func<Map, CreatureMapSystem?>? systems = null, ILogger? logger = null)
    {
        _options = options ?? new PetOptions();
        _systems = systems ?? (static map => map.FindUpdater<CreatureMapSystem>());
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The totem spell lookup (null: totems are visual only).</summary>
    public ITotemSpellSource? TotemSpells { get; set; }

    /// <summary>Register the totem effects on <paramref name="spells"/> (SpellEffects.cpp:4923-5007).</summary>
    public void Install(SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        _spells = spells;
        spells.RegisterEffect(SpellEffectName.SummonTotem, context => EffectSummonTotem(context, TotemSlots.None));
        spells.RegisterEffect(SpellEffectName.SummonTotemSlot1, context => EffectSummonTotem(context, TotemSlots.Fire));
        spells.RegisterEffect(SpellEffectName.SummonTotemSlot2, context => EffectSummonTotem(context, TotemSlots.Earth));
        spells.RegisterEffect(SpellEffectName.SummonTotemSlot3, context => EffectSummonTotem(context, TotemSlots.Water));
        spells.RegisterEffect(SpellEffectName.SummonTotemSlot4, context => EffectSummonTotem(context, TotemSlots.Air));
    }

    // --- totems ---------------------------------------------------------------------------------

    private void EffectSummonTotem(SpellEffectContext context, int slot)
        => SummonTotem(context.Caster, context.Spell, context.Effect, slot, context.Value, context.Spell.GetDuration());

    /// <summary>
    /// vmangos Spell::EffectSummonTotem (SpellEffects.cpp:4923-5007): unsummon the totem already in
    /// the slot, create the totem 2 yards from the caster at <c>pi/4 - slot * pi/2</c> from its
    /// facing, give it the owner's faction and level, the spell duration and (when the effect has a
    /// value) that value as health, then add it to the map with the spawn animation. Returns null
    /// when nothing could be summoned.
    /// </summary>
    public Creature? SummonTotem(Unit caster, SpellInfo spell, SpellEffectInfo effect, int slot, int health, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(effect);
        if (!TryGetSystems(caster, spell.Id, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return null;
        }

        // unsummon old totem
        if (slot < TotemSlots.Count && pets.GetTotem(caster, slot) is { } old)
        {
            Unsummon(old);
        }

        uint entry = (uint)effect.MiscValue;
        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            Warn($"totem-template:{entry}", "creature entry {Entry} does not exist but is used in spell {Spell} totem summon", entry, spell.Id);
            return null;
        }

        float angle = slot < TotemSlots.Count ? (MathF.PI / TotemSlots.Count) - (slot * 2 * MathF.PI / TotemSlots.Count) : 0f;
        Creature totem = creatures.SpawnSummoned(template, HighGuid.Unit, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Totem, caster.Guid, spell.Id, slot, durationMs);

            // Totem::SetOwner (Totem.cpp:152-158)
            creature.SetCreatorGuid(caster.Guid);
            creature.SetOwnerGuid(caster.Guid);
            creature.FactionTemplate = caster.FactionTemplate;
            creature.Level = caster.Level;
            creature.SetUInt32(UpdateFields.UnitCreatedBySpell, spell.Id);

            if (health != 0)
            {
                creature.MaxHealth = (uint)health;
                creature.Health = (uint)health;
            }

            if (caster is Player)
            {
                creature.UnitFlags |= UnitFlags.PlayerControlled;
            }

            if ((caster.UnitFlags & UnitFlags.Pvp) != 0)
            {
                creature.UnitFlags |= UnitFlags.Pvp;
            }

            (float x, float y) = ClosePoint(caster, creature.BoundingRadius, TotemDistance, caster.Orientation + angle);
            float z = creatures.GroundZ(x, y, caster.Z) ?? caster.Z;

            // totem must be at same Z in case swimming caster and etc.
            if (MathF.Abs(z - caster.Z) > TotemMaxZDifference)
            {
                z = caster.Z;
            }

            return new CreatureHome(x, y, z, caster.Orientation);
        });

        // A totem never fights on its own account; an active totem's targeting is TotemAI's (not part of this build).
        totem.AI = new NullCreatureAI(totem);
        pets.Options = _options;
        pets.Register(totem, this);
        SendSpawnAnimation(totem);
        CastTotemSpell(totem);
        return totem;
    }

    /// <summary>
    /// vmangos Totem::Summon (Totem.cpp:94-112): a passive totem (its spell has no cast time) casts
    /// its spell on itself at once. An active totem (a spell with a cast time) needs TotemAI, which
    /// waits for the creature-ai targeting primitive; without a spell source the totem is visual only.
    /// </summary>
    private void CastTotemSpell(Creature totem)
    {
        if (TotemSpells is not { } source)
        {
            Warn("totem-visual", "no totem spell source is registered: totems are visual only (creature_template.totem_spell_id is not in the content)");
            return;
        }

        uint spellId = source.GetTotemSpell(totem.Template);
        if (spellId == 0 || _spells is not { } spells || spells.Store.Get(spellId) is not { } spell)
        {
            return; // there are some totems, which exist just for their visual appearance
        }

        if (spell.GetCastTime(totem.Level) != 0)
        {
            Warn($"totem-active:{spellId}", "totem spell {Spell} has a cast time: an active totem needs TotemAI, which is not implemented; the totem stays idle", spellId);
            return;
        }

        spells.CastSpell(totem, spellId, SpellCastTargets.ForSelf(), triggered: true);
    }

    // --- unsummon -------------------------------------------------------------------------------

    /// <summary>
    /// Remove a summon from the world: the totem's despawn animation and the totem spell's auras
    /// (Totem::UnSummon, Totem.cpp:114-150), the owner's pet link for a pet (vmangos Pet::Unsummon),
    /// then the creature itself.
    /// </summary>
    public void Unsummon(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (creature.Summon is not { } links)
        {
            return;
        }

        Unit? owner = creature.GetOwner();
        Map? map = creature.Map;
        switch (links.Kind)
        {
            case SummonKind.Totem:
                SendDespawnAnimation(creature);
                RemoveTotemSpellAuras(creature, owner);
                break;
            case SummonKind.Pet:
                if (owner is not null && owner.PetGuid == creature.Guid)
                {
                    owner.SetPetGuid(ObjectGuid.Empty);
                }

                break;
        }

        map?.Pets?.Forget(creature);
        creature.System?.Despawn(creature);
    }

    private void RemoveTotemSpellAuras(Creature totem, Unit? owner)
    {
        if (TotemSpells is not { } source || _spells is not { } spells)
        {
            return;
        }

        uint spellId = source.GetTotemSpell(totem.Template);
        if (spellId == 0)
        {
            return;
        }

        spells.RemoveAuras(totem, spellId);
        if (owner is null)
        {
            return;
        }

        spells.RemoveAuras(owner, spellId);

        // remove aura all party members too
        if (owner is Player && owner.Map is { } map)
        {
            foreach (ObjectGuid member in spells.Groups.GetGroupMembers(owner, raid: false))
            {
                if (map.FindPlayer(member) is { } player && !ReferenceEquals(player, owner))
                {
                    spells.RemoveAuras(player, spellId);
                }
            }
        }
    }

    // --- pets (SPELL_EFFECT_SUMMON through the sink) -------------------------------------------------

    /// <inheritdoc/>
    public Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs)
        => Summon(caster, new SpellSummonRequest(0, entry, x, y, z, orientation, durationMs));

    /// <inheritdoc/>
    public Unit? Summon(Unit caster, in SpellSummonRequest request)
    {
        ArgumentNullException.ThrowIfNull(caster);

        // vmangos Spell::EffectSummon: a caster that already has a pet summons nothing.
        if (!caster.PetGuid.IsEmpty || !TryGetSystems(caster, request.SpellId, out PetMapSystem? pets, out CreatureMapSystem? creatures))
        {
            return null;
        }

        if (creatures.Content.FindTemplate(request.Entry) is not { } template)
        {
            Warn($"pet-template:{request.Entry}", "creature entry {Entry} not found for spell {Spell}", request.Entry, request.SpellId);
            return null;
        }

        SpellSummonRequest req = request;
        Creature pet = creatures.SpawnSummoned(template, HighGuid.Pet, creature =>
        {
            creature.Summon = new SummonLinks(SummonKind.Pet, caster.Guid, req.SpellId, TotemSlots.None, req.DurationMs);
            ApplyOwner(creature, caster, req.SpellId);

            // vmangos passes -caster orientation for the pet (SpellEffects.cpp:2372).
            return new CreatureHome(req.X, req.Y, req.Z, Creature.NormalizeOrientation(-caster.Orientation));
        });

        pets.Options = _options;
        pets.Register(pet, this);
        caster.SetPetGuid(pet.Guid);
        return pet;
    }

    /// <inheritdoc/>
    public bool CanSummon(Unit owner, uint entry)
        => owner.Map is { } map && _systems(map) is { } creatures && creatures.Content.FindTemplate(entry) is not null
            && map.Pets is not null && owner.PetGuid.IsEmpty;

    // --- shared pieces --------------------------------------------------------------------------

    /// <summary>The links a summoned pet or guardian gets from its owner (SpellEffects.cpp:2391-2397, 2880-2884).</summary>
    internal static void ApplyOwner(Creature creature, Unit owner, uint spellId)
    {
        creature.SetOwnerGuid(owner.Guid);
        creature.SetCreatorGuid(owner.Guid);
        creature.FactionTemplate = owner.FactionTemplate;
        creature.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, 0);
        creature.SetUInt32(UpdateFields.UnitCreatedBySpell, spellId);
        creature.NpcFlags = 0;
        creature.Level = owner.Level;
        if ((owner.UnitFlags & UnitFlags.Pvp) != 0)
        {
            creature.UnitFlags |= UnitFlags.Pvp;
        }
    }

    /// <summary>
    /// vmangos WorldObject::GetNearPoint's primary candidate (Object.cpp:2726-2790):
    /// <c>pos + (own radius + distance + searcher radius) * (cos, sin)(angle)</c>. The collision
    /// search for a free spot around the owner (ObjectPosSelector) and the line-of-sight retry are
    /// not ported (docs/integration/pets.md).
    /// </summary>
    internal static (float X, float Y) ClosePoint(Unit owner, float summonRadius, float distance, float absoluteAngle)
    {
        float range = owner.BoundingRadius + distance + summonRadius;
        return (owner.X + (range * MathF.Cos(absoluteAngle)), owner.Y + (range * MathF.Sin(absoluteAngle)));
    }

    private bool TryGetSystems(Unit caster, uint spellId, out PetMapSystem pets, out CreatureMapSystem creatures)
    {
        pets = null!;
        creatures = null!;
        if (caster.Map is not { } map)
        {
            return false;
        }

        PetMapSystem? petSystem = map.Pets;
        CreatureMapSystem? creatureSystem = _systems(map);
        if (petSystem is null || creatureSystem is null)
        {
            Warn($"no-system:{map.MapId}", "map {Map} has no creature or pet system: summon spell {Spell} cannot spawn", map.MapId, spellId);
            return false;
        }

        pets = petSystem;
        creatures = creatureSystem;
        return true;
    }

    /// <summary>vmangos Object::SendObjectSpawnAnim: SMSG_GAMEOBJECT_SPAWN_ANIM {guid} to every player within visibility range.</summary>
    internal static void SendSpawnAnimation(Creature creature) => SendAnimation(creature, WorldOpcode.SmsgGameobjectSpawnAnim);

    /// <summary>vmangos Object::SendObjectDeSpawnAnim.</summary>
    internal static void SendDespawnAnimation(Creature creature) => SendAnimation(creature, WorldOpcode.SmsgGameobjectDespawnAnim);

    private static void SendAnimation(Creature creature, WorldOpcode opcode)
    {
        if (creature.Map is not { } map)
        {
            return;
        }

        var writer = new PacketWriter(8);
        writer.WriteUInt64(creature.Guid.Value);
        map.BroadcastInRange(creature, Map.VisibilityRange, opcode, writer.AsSpan(), includeSelf: true);
    }

    private void Warn(string key, string message, params object[] args)
    {
        if (_warned.Add(key))
        {
            _logger.LogWarning(message, args);
        }
    }
}

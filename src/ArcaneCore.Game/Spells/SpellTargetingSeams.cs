using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Line of sight between two points of a map (vmangos <c>Map::isInLineOfSight</c> over vmaps).
/// The vmap/LoS area (branch <c>feat/vmap-los</c>) owns the real implementation; the spell system
/// only consumes this seam and defaults to <see cref="AlwaysVisibleLineOfSight"/>
/// (docs/integration/spells-persistence.md).
/// </summary>
public interface ILineOfSight
{
    /// <summary>Whether nothing static blocks the segment between the two points of <paramref name="map"/>.</summary>
    bool IsInLineOfSight(Map map, float x1, float y1, float z1, float x2, float y2, float z2);
}

/// <summary>Default <see cref="ILineOfSight"/>: no collision data, everything is visible.</summary>
public sealed class AlwaysVisibleLineOfSight : ILineOfSight
{
    public static readonly AlwaysVisibleLineOfSight Instance = new();

    public bool IsInLineOfSight(Map map, float x1, float y1, float z1, float x2, float y2, float z2) => true;
}

/// <summary>Who is an enemy or a friend of a caster when a spell picks implicit targets.</summary>
public interface ISpellTargetRelations
{
    /// <summary>vmangos Unit::IsValidAttackTarget (enemy AoE, chain damage).</summary>
    bool IsHostile(Unit caster, Unit target);

    /// <summary>vmangos Unit::IsFriendlyTo (friendly AoE, chain heal).</summary>
    bool IsFriendly(Unit caster, Unit target);
}

/// <summary>Default <see cref="ISpellTargetRelations"/>: the map's <see cref="CombatHooks"/> (factions arrive through them).</summary>
public sealed class CombatHookRelations : ISpellTargetRelations
{
    public static readonly CombatHookRelations Instance = new();

    public bool IsHostile(Unit caster, Unit target)
        => !ReferenceEquals(caster, target) && Hooks(caster).CanAttack(caster, target);

    public bool IsFriendly(Unit caster, Unit target)
        => ReferenceEquals(caster, target) || Hooks(caster).IsFriendly(caster, target);

    private static CombatHooks Hooks(Unit caster) => caster.Map?.FindUpdater<MapCombat>()?.Hooks ?? CombatHooks.Default;
}

/// <summary>
/// Group membership for party/raid targets and party area auras (vmangos Group / Player::GetGroup).
/// The social area owns groups; the world daemon adapts its GroupManager (SpellFeature).
/// </summary>
public interface ISpellGroupResolver
{
    /// <summary>
    /// GUIDs of the units sharing a group with <paramref name="unit"/>, the unit included.
    /// <paramref name="raid"/> false = the unit's party (its raid sub-group when in a raid).
    /// </summary>
    IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid);
}

/// <summary>Default <see cref="ISpellGroupResolver"/>: nobody is grouped, a unit's party is itself.</summary>
public sealed class NoGroupResolver : ISpellGroupResolver
{
    public static readonly NoGroupResolver Instance = new();

    public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return [unit.Guid];
    }
}

/// <summary>
/// SPELL_EFFECT_SUMMON (vmangos Spell::EffectSummon / DoSummon*). Creature AI and temporary
/// summons belong to the creatures area; until it installs a sink the effect is reported as
/// not implemented (a stub, docs/integration/spells-persistence.md).
/// </summary>
public interface ISpellSummonSink
{
    /// <summary>Summon creature <paramref name="entry"/> for <paramref name="durationMs"/> (-1/0 = until dismissed); returns the summon or null.</summary>
    Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs);
}

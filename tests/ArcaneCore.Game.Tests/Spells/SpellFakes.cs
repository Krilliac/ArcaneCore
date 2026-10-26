using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>Relations decided by GUID sets: hostile units listed in <see cref="Hostile"/>, everyone else friendly.</summary>
internal sealed class FakeRelations : ISpellTargetRelations
{
    public HashSet<ObjectGuid> Hostile { get; } = [];

    public bool IsHostile(Unit caster, Unit target) => !ReferenceEquals(caster, target) && Hostile.Contains(target.Guid) != Hostile.Contains(caster.Guid);

    public bool IsFriendly(Unit caster, Unit target) => ReferenceEquals(caster, target) || !IsHostile(caster, target);
}

/// <summary>
/// A vmap-los <see cref="ILineOfSight"/> with a wall at <see cref="WallX"/>: a segment is blocked
/// when its ends lie on different sides. Installed with <c>WorldCollision.Of(world).Install</c>.
/// </summary>
internal sealed class FakeLineOfSight : ILineOfSight
{
    public float WallX { get; set; } = float.MaxValue;

    public int Queries { get; private set; }

    public bool Enabled => true;

    public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
    {
        Queries++;
        return (from.X < WallX) == (to.X < WallX);
    }

    public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
    {
        hit = to;
        return false;
    }

    public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

    public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
    {
        info = default;
        return false;
    }
}

/// <summary>Explicit groups: each set is one party; <see cref="Raid"/> joins parties into one raid.</summary>
internal sealed class FakeGroups : ISpellGroupResolver
{
    public List<HashSet<ObjectGuid>> Parties { get; } = [];

    public bool Raid { get; set; }

    public IReadOnlyCollection<ObjectGuid> GetGroupMembers(Unit unit, bool raid)
    {
        if (raid && Raid)
        {
            HashSet<ObjectGuid> all = [.. Parties.SelectMany(p => p)];
            return all.Contains(unit.Guid) ? all : [unit.Guid];
        }

        return Parties.FirstOrDefault(p => p.Contains(unit.Guid)) is { } party ? party : [unit.Guid];
    }
}

/// <summary>Records summons and optionally fails them.</summary>
internal sealed class FakeSummons : ISpellSummonSink
{
    public List<(Unit Caster, uint Entry, float X, float Y, float Z, int Duration)> Calls { get; } = [];

    public bool Fail { get; set; }

    public bool Refuse { get; set; }

    public bool CanSummon(Unit owner, uint entry) => !Refuse;

    public Unit? Summon(Unit caster, uint entry, float x, float y, float z, float orientation, int durationMs)
    {
        Calls.Add((caster, entry, x, y, z, durationMs));
        return Fail ? null : caster;
    }
}

/// <summary>Combat rules with fixed outcomes.</summary>
internal sealed class FixedRules : ISpellCombatRules
{
    public SpellMissInfo Miss { get; set; }

    public bool Crit { get; set; }

    public float Multiplier { get; set; } = 1.5f;

    public float ResistFraction { get; set; }

    public uint Armor { get; set; }

    public SpellMissInfo RollHit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => Miss;

    public bool RollCrit(SpellSystem system, Unit caster, Unit target, SpellInfo spell) => Crit;

    public float CritMultiplier(SpellInfo spell) => Multiplier;

    public uint RollPartialResist(SpellSystem system, Unit caster, Unit target, SpellInfo spell, uint damage) => (uint)(damage * ResistFraction);

    public uint ApplyArmor(Unit caster, Unit target, SpellInfo spell, uint damage) => damage > Armor ? damage - Armor : 0;
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells.Procs.Talents;

/// <summary>The vmangos helpers the talent proc scripts share: dithering, the armor reduction of a probe hit and the random unfriendly target.</summary>
internal static class TalentProcSupport
{
    /// <summary>SPELL_SCHOOL_MASK_NORMAL: the armor school of MOD_TARGET_RESISTANCE (vmangos SpellCaster::CalcArmorReducedDamage).</summary>
    private const int NormalSchoolMask = 1;

    /// <summary>The largest bounding radius the grid search allows for (the exact range test adds both units' radii).</summary>
    private const float SearchSlack = 10f;

    /// <summary>vmangos <c>rand_dither</c> (Utilities/Random.cpp:80-83): the integer part, plus one with the probability of the fraction, sign kept.</summary>
    public static int Dither(float value, Random random) => (int)MathF.CopySign(MathF.Floor(MathF.Abs(value) + random.NextSingle()), value);

    /// <summary>vmangos <c>rand_ditheru</c>: <see cref="Dither"/> of a value clamped to 0.</summary>
    public static int DitherUnsigned(float value, Random random) => Dither(MathF.Max(value, 0f), random);

    /// <summary>A custom base point of 0 is no custom base point (vmangos TriggerProccedSpell casts the plain spell when all are 0).</summary>
    public static int? BasePoints(int value) => value != 0 ? value : null;

    /// <summary>
    /// vmangos <c>SpellCaster::CalcArmorReducedDamage(pVictim, damage)</c> (SpellCaster.cpp:1121-1145) as the attacker computes it: the victim's
    /// armor plus the attacker's physical MOD_TARGET_RESISTANCE, at the attacker's level, at least 1.
    /// </summary>
    public static uint ArmorReducedDamage(SpellSystem system, Unit attacker, Unit victim, uint damage)
    {
        float armor = victim.GetInt32(UpdateFields.UnitFieldResistances)
            + system.GetTotalAuraModifier(attacker, AuraType.ModTargetResistance, a => (a.MiscValue & NormalSchoolMask) != 0);
        return MeleeHitTable.ApplyArmor(damage, armor, attacker.Level);
    }

    /// <summary>
    /// The damage of a hit before the victim's armor (Sweeping Strikes, Blade Flurry: "Reconstitute damage before armor reduction",
    /// <c>rand_ditheru(amount * 100 / CalcArmorReducedDamage(pVictim, 100))</c>).
    /// </summary>
    public static int DamageBeforeArmor(SpellSystem system, Unit attacker, Unit victim, uint amount)
        => DitherUnsigned(amount * 100f / ArmorReducedDamage(system, attacker, victim, 100), system.Random);

    /// <summary>vmangos <c>Unit::GetHealthPercent</c>.</summary>
    public static float HealthPercent(Unit unit) => unit.MaxHealth == 0 ? 0f : unit.Health * 100f / unit.MaxHealth;

    /// <summary>
    /// vmangos <c>Unit::SelectRandomUnfriendlyTarget(except, radius, inFront = false, isValidAttackTarget, notPvpEnabling)</c> (Unit.cpp:9509-9538)
    /// called on <paramref name="owner"/>: a random living unit within <paramref name="radius"/> of the owner (both bounding radii added, vmangos
    /// IsWithinDistInMap) that is not friendly to it (AnyUnfriendlyUnitInObjectRangeCheck), not <paramref name="except"/>, in the owner's line of
    /// sight, optionally one it may attack and one it may attack without turning PvP on (CanAttackWithoutEnablingPvP). Candidates come in grid
    /// order and <see cref="SpellSystem.Random"/> picks one, so a seeded run picks the same one.
    /// </summary>
    public static Unit? SelectRandomUnfriendlyTarget(SpellSystem system, Unit owner, Unit? except, float radius, bool validAttackTarget, bool notPvpEnabling)
    {
        if (owner.Map is not { } map)
        {
            return null;
        }

        var objects = new List<WorldObject>();
        map.Grids.CollectObjects(owner.X, owner.Y, radius + owner.BoundingRadius + SearchSlack, objects);
        var seen = new HashSet<Unit>(ReferenceEqualityComparer.Instance);
        var candidates = new List<Unit>();
        foreach (WorldObject obj in objects)
        {
            if (obj is not Unit unit || ReferenceEquals(unit, owner) || ReferenceEquals(unit, except) || !seen.Add(unit)
                || !unit.IsInWorld || !ReferenceEquals(unit.Map, map) || !unit.IsAlive || !IsWithinDistance(owner, unit, radius)
                || system.Relations.IsFriendly(owner, unit) || !map.Collision.IsWithinLineOfSight(owner, unit)
                || (validAttackTarget && !system.Relations.IsHostile(owner, unit))
                || (notPvpEnabling && !CanAttackWithoutEnablingPvP(owner, unit)))
            {
                continue;
            }

            candidates.Add(unit);
        }

        return candidates.Count == 0 ? null : candidates[system.Random.Next(candidates.Count)];
    }

    /// <summary>vmangos WorldObject::IsWithinDistInMap (3D): the centres within the range plus both bounding radii.</summary>
    private static bool IsWithinDistance(Unit a, Unit b, float range)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float reach = range + a.BoundingRadius + b.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }

    /// <summary>
    /// vmangos Unit::CanAttackWithoutEnablingPvP (Unit.cpp:9985-9993): a player-controlled target is only fair game when the attacking player is
    /// PvP-flagged, both are free-for-all flagged, or they duel.
    /// </summary>
    private static bool CanAttackWithoutEnablingPvP(Unit attacker, Unit target)
    {
        if (target.GetCharmerOrOwnerPlayerOrSelf() is not { } attacked || attacker.GetCharmerOrOwnerPlayerOrSelf() is not { } player)
        {
            return true;
        }

        bool ffa = (player.Flags & PlayerFlags.FfaPvp) != 0 && (attacked.Flags & PlayerFlags.FfaPvp) != 0;
        return (player.UnitFlags & UnitFlags.Pvp) != 0 || ffa || DuelRules.IsInDuelWith(player, attacked);
    }
}

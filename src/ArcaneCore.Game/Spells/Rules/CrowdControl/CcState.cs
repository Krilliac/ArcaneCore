using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells.Rules.CrowdControl;

/// <summary>
/// Crowd-control state derived from the live auras of a unit: the unit flags (stunned, silenced, pacified,
/// disarmed, fleeing, confused) and the root of a player. Every flag is recomputed from the auras that are
/// still on the unit rather than toggled, so overlapping auras and a held logout stun cannot desynchronise it
/// (vmangos checks <c>HasAuraType</c> before clearing a flag, SpellAuras.cpp:3535,3631,3881,9132-9146).
/// </summary>
internal static class CcState
{
    /// <summary>CreatureType.dbc totem (11) as a creature-type mask bit (vmangos Creature::IsTotem, Unit::ModConfuseSpell Unit.cpp:9140).</summary>
    private const uint TotemTypeMask = 1u << 10;

    public static bool IsTotem(Unit unit) => unit is Creatures.Creature && unit.CreatureTypeMask() == TotemTypeMask;

    /// <summary>Who frightened a creature (the caster of its latest fear aura): the creature movement flees from it. Weak: the caster may leave the world.</summary>
    private static readonly ConditionalWeakTable<Unit, WeakReference<Unit>> s_fearSources = new();

    /// <summary>
    /// The unit a feared creature runs from (vmangos SetFeared's casterGuid), or null when unknown, gone, or the creature fears
    /// itself. Read by the creature movement hook when it starts a flight.
    /// </summary>
    public static Unit? FearSource(Unit unit)
        => s_fearSources.TryGetValue(unit, out WeakReference<Unit>? source) && source.TryGetTarget(out Unit? caster) && caster.IsInWorld
            ? caster
            : null;

    /// <summary>Record the caster of a fear aura just applied to a creature (the latest one wins).</summary>
    public static void RememberFearSource(Unit unit, Unit? caster)
    {
        s_fearSources.Remove(unit);
        if (caster is not null && !ReferenceEquals(caster, unit))
        {
            s_fearSources.Add(unit, new WeakReference<Unit>(caster));
        }
    }

    public static bool IsMounted(Unit unit) => unit.GetUInt32(UpdateFields.UnitFieldMountdisplayid) != 0;

    private static void SetFlag(Unit unit, UnitFlags flag, bool on)
    {
        if (on)
        {
            unit.UnitFlags |= flag;
        }
        else
        {
            unit.UnitFlags &= ~flag;
        }
    }

    /// <summary>UNIT_FLAG_STUNNED: a live stun aura (not while on a taxi, SpellAuras.cpp:3556) or a requested logout.</summary>
    public static void RefreshStun(SpellSystem system, Unit unit)
    {
        bool byAura = system.HasLiveAura(unit, AuraType.ModStun) && (unit.UnitFlags & UnitFlags.TaxiFlight) == 0;
        if (unit is Player player)
        {
            player.StunnedByAura = byAura;
        }

        SetFlag(unit, UnitFlags.Stunned, byAura || unit is Player { IsLoggingOut: true });
    }

    /// <summary>
    /// A player's root: rooted while a root or stun aura (or a requested logout) holds it; lifted otherwise,
    /// except on a dead player whose root belongs to the combat death flow (set on JUST_DIED, lifted by release or resurrection).
    /// </summary>
    public static void RefreshRoot(SpellSystem system, Unit unit)
    {
        if (unit is Creatures.Creature creature)
        {
            // A creature has no client to order: the server-owned root flag is what its movement code reads
            // (CreatureMovementGates, the fear and confuse generators). The client-facing root packet of a creature
            // is not sent here (UNVERIFIED for 1.12.1, docs/areas/creature-movement-spawns.md).
            if (system.IsRooted(creature) || creature.AiImmobilized)
            {
                creature.AddMovementFlags(MovementFlags.Root);
            }
            else
            {
                creature.RemoveMovementFlags(MovementFlags.Root);
            }

            return;
        }

        if (unit is not Player player)
        {
            return;
        }

        bool byAura = system.IsRooted(player);
        player.RootedByAura = byAura;
        if (byAura || player.IsLoggingOut)
        {
            player.SetRooted(true);
        }
        else if (player.IsAlive)
        {
            player.SetRooted(false);
        }
    }

    /// <summary>UNIT_FLAG_SILENCED: any live silence or pacify-and-silence aura.</summary>
    public static void RefreshSilence(SpellSystem system, Unit unit) =>
        SetFlag(unit, UnitFlags.Silenced, system.HasLiveAura(unit, AuraType.ModSilence, AuraType.ModPacifySilence));

    /// <summary>UNIT_FLAG_PACIFIED: any live pacify or pacify-and-silence aura.</summary>
    public static void RefreshPacify(SpellSystem system, Unit unit) =>
        SetFlag(unit, UnitFlags.Pacified, system.HasLiveAura(unit, AuraType.ModPacify, AuraType.ModPacifySilence));

    /// <summary>UNIT_FLAG_DISARMED: any live disarm aura.</summary>
    public static void RefreshDisarm(SpellSystem system, Unit unit) =>
        SetFlag(unit, UnitFlags.Disarmed, system.HasLiveAura(unit, AuraType.ModDisarm));

    /// <summary>
    /// UNIT_FLAG_CONFUSED / UNIT_FLAG_FLEEING (vmangos Unit::ModConfuseSpell, Unit.cpp:9132-9146): a live
    /// confuse aura, and a live fear aura unless the unit prevents fleeing. Totems never get either.
    /// </summary>
    public static void RefreshFear(SpellSystem system, Unit unit)
    {
        if (IsTotem(unit))
        {
            return;
        }

        SetFlag(unit, UnitFlags.Confused, system.HasLiveAura(unit, AuraType.ModConfuse));
        bool feared = system.HasLiveAura(unit, AuraType.ModFear) && !system.HasLiveAura(unit, AuraType.PreventsFleeing);
        SetFlag(unit, UnitFlags.Fleeing, feared);
        if (unit is Creatures.Creature creature)
        {
            creature.FearHeldByAura = feared; // a creature's timed flight shares the flag and must not clear it while this holds
        }
    }
}

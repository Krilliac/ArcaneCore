using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Feign Death (hunter lane): vmangos Aura::HandleFeignDeath (SpellAuras.cpp:3460-3500) and
/// Unit::SetFeignDeath (Unit.cpp:9228-9278), built on this system's cast and aura state. The
/// "feigning" state (vmangos UNIT_STATE_FEIGN_DEATH) is held by the aura object, so it goes with
/// the aura; read it with <see cref="IsFeigningDeath"/>.
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>AURA_INTERRUPT_STEALTH_INVIS_CANCELS (vmangos SpellDefines.h:597): stealth and invisibility auras carry it.</summary>
    private const uint StealthInvisCancelsFlag = 0x00100000;

    /// <summary>Spell range beyond which no spell is cast (vmangos Unit::InterruptSpellsCastedOnMe: 100 yd).</summary>
    private const float InterruptSpellsRange = 100.0f;

    /// <summary>Auras whose Feign Death succeeded (the aura object is the key, so the state ends with it).</summary>
    private readonly ConditionalWeakTable<SpellAura, object> _feignSucceeded = new();

    /// <summary>
    /// vmangos Unit::IsFeigningDeathSuccessfully: the unit is alive and holds a Feign Death aura that
    /// was not resisted. Creature AI (target validity, aggro), movement and the player interaction
    /// handlers read this (docs/integration/hunter.md).
    /// </summary>
    public bool IsFeigningDeath(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.IsAlive)
        {
            return false;
        }

        foreach (SpellAuraHolder holder in GetAuras(unit))
        {
            if (holder.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.FeignDeath } && _feignSucceeded.TryGetValue(aura, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos RemoveSpellsCausingAura(SPELL_AURA_FEIGN_DEATH): twenty-odd handlers (auction house, vendors,
    /// quest givers, taxi, trade, guild, petitions ...) end the feign when the player acts on the world.
    /// </summary>
    public void BreakFeignDeath(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (GetState(unit.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => h.HasAura(AuraType.FeignDeath)).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }

    /// <summary>The aura handler for AuraType.FeignDeath (registered by <see cref="RangedAuras"/>).</summary>
    internal void ApplyFeignDeath(SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (!apply)
        {
            _feignSucceeded.Remove(aura);
            target.RemoveFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);
            return;
        }

        // 1.7.0: players are never resisted; only creatures that have the feigner on their threat list
        // roll, and only within their attack distance (a creature owned by a player is skipped: pets lane).
        bool success = true;
        foreach (Unit attacker in target.Combat.ThreatenedBy.ToArray())
        {
            if (attacker is not Creature creature)
            {
                continue;
            }

            float distance = creature.Map?.FindUpdater<CreatureMapSystem>()?.GetAttackDistance(creature, target) ?? 30.0f;
            if (IsWithinDistInMap(creature, target, distance) && FeignResisted(target, creature))
            {
                success = false;
                break;
            }
        }

        // Unit::SetFeignDeath: stop moving and turning.
        MovementInfo movement = target.Movement;
        (movement.X, movement.Y, movement.Z, movement.Orientation) = (target.X, target.Y, target.Z, target.Orientation); // the position is the truth, not the last packet
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        target.ApplyMovement(movement, NowMs);

        if (!success)
        {
            // The client is told, the attack swing is cancelled (SendAttackSwingCancelAttack: SMSG_CANCEL_COMBAT); no combat stop.
            if (target is Player player)
            {
                player.Session.Send(WorldOpcode.SmsgFeignDeathResisted, []);
                player.Session.Send(WorldOpcode.SmsgCancelCombat, []);
            }
        }
        else
        {
            _feignSucceeded.Add(aura, holder);
            InterruptSpellsCastedOn(target);
            target.Map?.Combat.CombatStop(target);
            RemoveHoldersWithAuraInterrupt(target, StealthInvisCancelsFlag);
            foreach (Unit attacker in target.Combat.ThreatenedBy.ToArray())
            {
                attacker.Combat.Threat.Remove(target); // HostileRefManager::deleteReferences
            }
        }

        target.SetFlag(UpdateFields.UnitDynamicFlags, UnitDynFlags.Dead);

        // The feigner's own cast: a normal cast completes silently ("prevent interrupt message"), anything else is interrupted.
        if (GetState(target.Guid)?.CurrentCast is { } current)
        {
            if (holder.CasterGuid == target.Guid && current.State == SpellCastState.Preparing)
            {
                Finish(current);
            }
            else
            {
                Cancel(current);
            }
        }
    }

    /// <summary>
    /// The resist roll (vmangos SpellCaster::MagicSpellHitResult with the feigner as caster): the
    /// magic hit chance for the level difference, at least 22%, plus the feigner's spell hit
    /// modifiers, kept within 1-99%; rolled against 0-10000 (irand).
    /// </summary>
    private bool FeignResisted(Unit feigner, Creature creature)
    {
        if (!creature.IsAlive)
        {
            return false;
        }

        float hit = Math.Max(22.0f, VanillaSpellCombatRules.MagicHitChance(feigner, creature)) + GetTotalAuraModifier(feigner, AuraType.ModSpellHitChance);
        int hitChance = Math.Clamp((int)(hit * 100.0f), 100, 9900);
        return Random.Next(0, 10_001) < 10_000 - hitChance;
    }

    /// <summary>vmangos WorldObject::IsWithinDistInMap: same map, 3D distance below the limit plus both bounding radii.</summary>
    private static bool IsWithinDistInMap(Unit a, Unit b, float distance)
    {
        if (!ReferenceEquals(a.Map, b.Map))
        {
            return false;
        }

        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float max = distance + a.BoundingRadius + b.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) < max * max;
    }

    /// <summary>
    /// vmangos Unit::InterruptSpellsCastedOnMe (defaults): within 100 yd, every non-friendly unit whose
    /// cast bar (with a cast time) or channel is aimed at <paramref name="victim"/> is interrupted.
    /// </summary>
    private void InterruptSpellsCastedOn(Unit victim)
    {
        foreach (UnitSpellState state in _states.Values.ToArray())
        {
            Unit unit = state.Unit;
            if (ReferenceEquals(unit, victim) || state.CurrentCast is not { } cast || cast.State == SpellCastState.Finished
                || cast.Targets.Unit != victim.Guid || !ReferenceEquals(unit.Map, victim.Map)
                || Distance3D(unit, victim.X, victim.Y, victim.Z) > InterruptSpellsRange || Relations.IsFriendly(unit, victim))
            {
                continue;
            }

            if ((cast.State == SpellCastState.Preparing && cast.CastTime > 0) || cast.State == SpellCastState.Casting)
            {
                Cancel(cast);
            }
        }
    }

    /// <summary>vmangos Unit::RemoveAurasWithInterruptFlags: remove every holder whose spell carries any of <paramref name="flags"/>.</summary>
    private void RemoveHoldersWithAuraInterrupt(Unit target, uint flags)
    {
        if (GetState(target.Guid) is not { } state)
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => ((uint)h.Spell.AuraInterruptFlags & flags) != 0).ToArray())
        {
            RemoveHolder(state, holder);
        }
    }
}

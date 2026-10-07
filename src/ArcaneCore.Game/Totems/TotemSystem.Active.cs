using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Totems;

public sealed partial class TotemSystem
{
    /// <summary>
    /// Active cast-time totems (Searing): vmangos AI/TotemAI.cpp:66-125, Unit.h:1066-1074,
    /// Maps/GridNotifiers.h:880-898 and Unit.cpp:9985-9992 at 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa.
    /// The existing map updater owns both lifetime and casting; NullAI continues to suppress melee/chase/aggro.
    /// </summary>
    private void UpdateActiveTotem(TotemInfo info, Map map)
    {
        Unit totem = info.Creature;
        totem.AddMovementFlags(MovementFlags.Root);
        if (info.SpellId == 0 || _spells.Store.Get(info.SpellId) is not { CastTime.Base: > 0 } spell
            || _spells.GetState(totem.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting })
        {
            return;
        }

        Unit? victim = info.ActiveVictim.IsEmpty ? null : map.FindObject(info.ActiveVictim) as Unit;
        // A retained victim has priority. Only when it is absent does the owner's combat helper get a turn.
        victim ??= info.Owner.Combat.Victim ?? info.Owner.Combat.Attackers.FirstOrDefault();
        if (victim is null || !CanTarget(info, map, spell, victim))
        {
            victim = FindNearestEnemy(info, map, spell);
        }

        info.ActiveVictim = victim?.Guid ?? default;
        if (victim is null)
        {
            return;
        }

        totem.Orientation = MathF.Atan2(victim.Y - totem.Y, victim.X - totem.X);
        // Normal casts retain their real cast time, cooldown, power, target and line-of-sight checks.
        _spells.CastSpell(totem, info.SpellId, SpellCastTargets.ForUnit(victim.Guid), triggered: false);
    }

    private Unit? FindNearestEnemy(TotemInfo info, Map map, SpellInfo spell)
    {
        Unit totem = info.Creature;
        var candidates = new List<WorldObject>();
        map.Grids.CollectObjects(totem.X, totem.Y,
            spell.Range.Max + totem.BoundingRadius + map.Grids.MaxBoundingRadius, candidates);
        Unit? nearest = null;
        float nearestDistance = float.PositiveInfinity;
        var seen = new HashSet<Unit>(ReferenceEqualityComparer.Instance);
        foreach (WorldObject obj in candidates)
        {
            if (obj is not Unit candidate || !seen.Add(candidate) || !CanTarget(info, map, spell, candidate)
                || !_spells.Relations.IsHostile(totem, candidate) || !map.Combat.Hooks.IsHostileTo(totem, candidate)
                || !CanAcquireWithoutEnablingPvp(info.Owner, candidate))
            {
                continue;
            }

            // WorldObject::GetDistance takes off both bounding radii. Keep deterministic grid order for ties.
            float distance = MathF.Max(0, Distance(totem, candidate) - totem.BoundingRadius - candidate.BoundingRadius);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = candidate;
            }
        }

        return nearest;
    }

    private bool CanTarget(TotemInfo info, Map map, SpellInfo spell, Unit target)
    {
        Unit totem = info.Creature;
        if (ReferenceEquals(target, info.Owner) || !target.IsAlive || !target.IsInWorld || !ReferenceEquals(target.Map, map)
            || !map.Combat.Hooks.CanAttack(totem, target) || !map.Combat.Hooks.CanAttack(info.Owner, target))
        {
            return false;
        }

        // IsWithinDistInMap uses a strict comparison including both bounding radii.
        if (Distance(totem, target) >= spell.Range.Max + totem.BoundingRadius + target.BoundingRadius)
        {
            return false;
        }

        // TotemAI asks IsVisibleForOrDetect with detect=false. Creatures cannot acquire stealthed units
        // even at point-blank range; the caster's own stalk aura is the earlier visibility exception.
        if (StealthServices.Find(map) is { } stealth && stealth.Registry.VisibilityOf(target) != StealthVisibility.On
            && !_spells.GetAuras(target).Any(h => !h.IsRemoved && h.CasterGuid == totem.Guid
                && h.Auras.Any(a => a is not null && a.Type == AuraType.ModStalked)))
        {
            return false;
        }

        // Retail selection does not test LOS for an ordinary visible target; the normal cast checks do.
        return true;
    }

    private static bool CanAcquireWithoutEnablingPvp(Unit owner, Unit target)
    {
        Player? ownerPlayer = DuelRules.ControllingPlayer(owner);
        Player? targetPlayer = DuelRules.ControllingPlayer(target);
        return ownerPlayer is null || targetPlayer is null || (ownerPlayer.UnitFlags & UnitFlags.Pvp) != 0
            || DuelRules.IsInDuelWith(ownerPlayer, targetPlayer);
    }

    private static float Distance(Unit left, Unit right)
    {
        float dx = left.X - right.X;
        float dy = left.Y - right.Y;
        float dz = left.Z - right.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private bool IsOwnerValid(TotemInfo info, Map map)
        => info.Owner.IsInWorld && (info.Owner is Creature || info.Owner.IsAlive)
            && (!Options.OwnerLeash || (ReferenceEquals(info.Owner.Map, map)
                && Map.IsWithinVisibilityDistance(info.Owner, info.Creature, alreadyVisible: false)));

    /// <summary>
    /// The global spell update can run before the map updater. Recheck owner lifetime at preparation and
    /// completion, reproducing Totem::Update's owner check before Creature::Update even on a long world tick.
    /// Expiry is deliberately not checked here: vmangos allows the last creature update before expiry.
    /// </summary>
    private sealed class TotemOwnerCastCheck(TotemSystem system) : ISpellCastCheck
    {
        public SpellCheckPhase Phase => SpellCheckPhase.Start;

        public int Order => int.MinValue;

        public SpellCastResult Check(in SpellCastCheckContext context)
            => TotemQuery.TryGet(context.Caster, out TotemInfo info)
                && (context.Caster.Map is not { } map || !system.IsOwnerValid(info, map))
                    ? SpellCastResult.CasterDead
                    : SpellCastResult.CastOk;
    }
}

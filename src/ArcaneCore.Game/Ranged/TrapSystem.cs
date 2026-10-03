using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// Hunter traps (type 6 game objects created by SummonObjectSlot spells) of one map: the arming
/// delay, the per-update scan for the nearest valid target inside the trigger radius, the trap's
/// spell cast as its owner, the cooldown, the custom animation and the charges (vmangos
/// GameObject::Update, GameObject.cpp:340-360 and 455-600). Attached to the map as an
/// <see cref="IMapUpdater"/> after the <see cref="SpellObjectSystem"/>.
/// <para>
/// Not modelled: environmental traps (no owner; they cast from the game object, which the spell
/// system cannot do), battleground traps, totems counting at a third of the radius, stealthed
/// traps being hidden from enemies, pets and charmed units for the owner PvP rule (the port has no
/// owner or charmer link on units), and reflection exemption of traps (docs/areas/hunter.md).
/// </para>
/// </summary>
public sealed class TrapSystem(SpellSystem spells) : IMapUpdater
{
    /// <summary>
    /// The "hostile" half of the in-combat-or-hostile test (GameObject.cpp:300-302). Retail
    /// (<see cref="TrapHostilityRule.Faction"/>): the map's <see cref="Combat.CombatHooks.IsHostileTo"/>, the faction
    /// reaction, so neutral creatures do not trigger traps. The deviation <see cref="TrapHostilityRule.AttackTarget"/> uses
    /// the attack-target relation instead.
    /// </summary>
    private bool IsHostileTo(Map map, Unit owner, Unit target)
        => spells.RangedOptions.Traps.Hostility == TrapHostilityRule.AttackTarget
            ? spells.Relations.IsHostile(owner, target)
            : map.Combat.Hooks.IsHostileTo(owner, target);

    public void Update(Map map, uint diffMs)
    {
        if (map.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            return;
        }

        long clock = objects.ClockMs;
        foreach (SpellCreatedObject entry in spells.SpellObjects.All.ToArray())
        {
            GameObject go = entry.Object;
            if (go.Type != GameObjectType.Trap || !ReferenceEquals(go.System, objects) || !go.IsSpawned
                || go.LootState == GameObjectLootState.JustDeactivated)
            {
                continue;
            }

            if (!entry.Armed)
            {
                // GO_NOT_READY → GO_READY: the arming time (vmangos applies startDelay to every trap).
                entry.Armed = true;
                if (go.Template.GetData(TrapRules.StartDelayData) is var delay and not 0)
                {
                    go.CooldownUntilMs = clock + (delay * 1000L);
                }
            }

            if (go.CooldownUntilMs >= clock)
            {
                continue;
            }

            uint charges = go.Template.GetData(TrapRules.ChargesData);
            float radius = TrapRules.TriggerRadius(go.Template, spells.RangedOptions.Traps.RadiusSource);
            if (charges == 0 || radius <= 0 || !entry.Owner.IsInWorld || !ReferenceEquals(entry.Owner.Map, map))
            {
                continue; // the environmental branch (no owner, or no charges) is not modelled
            }

            Unit? target = FindTarget(map, go, entry.Owner, radius);
            if (target is null)
            {
                continue;
            }

            if (go.Template.GetData(TrapRules.SpellData) is var spellId and not 0)
            {
                spells.CastFromObject(entry.Owner, spellId, target);
            }

            uint cooldown = go.Template.GetData(TrapRules.CooldownData);
            go.CooldownUntilMs = clock + ((cooldown != 0 ? cooldown : TrapRules.DefaultCooldownSeconds) * 1000L);
            go.UseCount++;
            if (TrapRules.CustomAnimDisplays.Contains(go.Template.DisplayId))
            {
                map.BroadcastToObservers(go, WorldOpcode.SmsgGameobjectCustomAnim, GameObjectPackets.CustomAnim(go.Guid, 0));
            }

            if (go.UseCount >= charges)
            {
                go.UseCount = 0;
                go.LootState = GameObjectLootState.JustDeactivated; // the object system removes it on its next update
            }
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }

    /// <summary>
    /// HunterTrapTargetSelectorCheck over the units around the trap: alive, within the radius (3D, both
    /// bounding radii), allowed by the PvP rule of the owner, attackable by the owner and in combat or
    /// hostile to it. The nearest wins (the vmangos searcher narrows its range with every accepted unit).
    /// </summary>
    private Unit? FindTarget(Map map, GameObject trap, Unit owner, float radius)
    {
        var objects = new List<WorldObject>();
        map.Grids.CollectObjects(trap.X, trap.Y, radius + 5.0f, objects);
        Unit? best = null;
        float bestDistance = float.MaxValue;
        var seen = new HashSet<Unit>(ReferenceEqualityComparer.Instance);
        foreach (WorldObject obj in objects)
        {
            if (obj is not Unit unit || !unit.IsInWorld || !ReferenceEquals(unit.Map, map) || !unit.IsAlive || !seen.Add(unit)
                || ReferenceEquals(unit, owner) || !TrapRules.OwnerMayTrigger(owner, unit))
            {
                continue;
            }

            float dx = trap.X - unit.X;
            float dy = trap.Y - unit.Y;
            float dz = trap.Z - unit.Z;
            float reach = radius + trap.BoundingRadius + unit.BoundingRadius;
            if (((dx * dx) + (dy * dy) + (dz * dz)) >= reach * reach)
            {
                continue;
            }

            if (!spells.Relations.IsHostile(owner, unit) || !(unit.Combat.IsInCombat || IsHostileTo(map, owner, unit)))
            {
                continue;
            }

            float distance = trap.DistanceTo(unit);
            if (distance < bestDistance)
            {
                best = unit;
                bestDistance = distance;
            }
        }

        return best;
    }
}

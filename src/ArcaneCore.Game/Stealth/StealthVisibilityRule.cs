using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// The stealth part of vmangos Unit::IsVisibleForOrDetect (Unit.cpp:6321-6461), in the same order: a unit that is not stealthed,
/// the viewer itself, a game master, the Hunter's Mark caster and a non-hostile group member are always allowed; a unit in the
/// NO_DETECT group is hidden; an ordinary (movement-driven) evaluation keeps only units the viewer already sees; a detection
/// evaluation runs the distance formula (<see cref="StealthDetection"/>) and then line of sight. A dead viewer never detects.
/// Invisibility masks (potions, devices) are not modelled (docs/areas/rogue.md).
/// </summary>
public sealed class StealthVisibilityRule : IVisibilityRule
{
    private readonly SpellSystem _spells;
    private readonly StealthRegistry _registry;
    private readonly StealthOptions _options;

    public StealthVisibilityRule(SpellSystem spells, StealthRegistry registry, StealthOptions? options = null)
    {
        _spells = spells ?? throw new ArgumentNullException(nameof(spells));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? StealthOptions.Default;
    }

    public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
    {
        if (target is not Unit unit || ReferenceEquals(viewer, target))
        {
            return true;
        }

        StealthVisibility group = _registry.VisibilityOf(unit);
        if (group == StealthVisibility.On)
        {
            return true;
        }

        // if the viewer is dead it can't detect anyone
        if (!viewer.IsAlive)
        {
            detect = false;
        }

        if (viewer.IsGameMaster)
        {
            return true;
        }

        // Hunter's Mark makes the target always visible to its caster.
        if (_spells.GetAuras(unit).Any(h => !h.IsRemoved && h.CasterGuid == viewer.Guid
            && h.Auras.Any(a => a is not null && a.Type == AuraType.ModStalked)))
        {
            return true;
        }

        // a non-hostile group or raid member always sees a stealthed player
        if (unit is Player && !_spells.Relations.IsHostile(viewer, unit) && _spells.Groups.GetGroupMembers(unit, raid: true).Contains(viewer.Guid))
        {
            return true;
        }

        // unit got in stealth in this moment and must ignore old detected state
        if (group == StealthVisibility.NoDetect)
        {
            return false;
        }

        // players detect stealthed units only in the periodic detection pass
        if (!detect)
        {
            return alreadyVisible;
        }

        float dx = viewer.X - unit.X;
        float dy = viewer.Y - unit.Y;
        float dz = viewer.Z - unit.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (!StealthDetection.CanDetectStealthOf(_spells, viewer, unit, distance, _options).Detected)
        {
            return false;
        }

        return viewer.Map is not { } map || map.Collision.IsWithinLineOfSight(viewer, unit);
    }
}

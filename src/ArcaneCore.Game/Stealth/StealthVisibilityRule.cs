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
/// Invisibility uses the same per-viewer rule, with matching type masks and detection aura levels
/// (vmangos Unit.cpp:6401-6435,6502-6541).
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

    /// <summary>vmangos Player::IsGroupVisibleFor (Player.cpp:2924-2935): mode 0 same sub-group, 1 same raid, 2 same team.</summary>
    private bool IsGroupVisibleFor(Player stealthed, Player viewer) => _options.GroupVisibilityMode switch
    {
        StealthGroupVisibility.SameRaid => _spells.Groups.GetGroupMembers(stealthed, raid: true).Contains(viewer.Guid),
        StealthGroupVisibility.SameTeam => stealthed.Team == viewer.Team,
        _ => _spells.Groups.GetGroupMembers(stealthed, raid: false).Contains(viewer.Guid),
    };

    public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
    {
        if (!_registry.AnyHidden || target is not Unit unit || ReferenceEquals(viewer, target))
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

        // a non-hostile group-visible player always sees a stealthed player (Player::IsGroupVisibleFor, mode by Visibility.GroupMode)
        if (unit is Player stealthed && !_spells.Relations.IsHostile(viewer, unit) && IsGroupVisibleFor(stealthed, viewer))
        {
            return true;
        }

        // vmangos Unit.cpp:6401-6435: a shared invisibility type or sufficient detection reveals it.
        bool invisible = _spells.HasAuraType(unit, AuraType.ModInvisibility)
            && !InvisibilityAuras.CanDetect(_spells, viewer, unit);

        // unit got in stealth or invisibility in this moment and must ignore old detected state
        if (group == StealthVisibility.NoDetect)
        {
            return false;
        }

        if (invisible)
        {
            return false;
        }

        if (group == StealthVisibility.Invisibility)
        {
            return true;
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

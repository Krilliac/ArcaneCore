using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Stealth;

/// <summary>
/// The visibility services of one map (installed by the world daemon's StealthFeature): what other areas call to ask whether a
/// creature sees a stealthed or invisible player. Creature AI asks it in its line-of-sight/aggro check
/// (vmangos GridNotifiersImpl.h:54-70 CallAIMoveLOS: <c>moving->IsVisibleForOrDetect(creature, creature, true, false, &amp;alert)</c>;
/// a creature always detects, there is no "already visible" shortcut). Without an installed instance nothing is ever hidden from a
/// creature, which is the behaviour before the stealth lane (docs/areas/creature-ai.md).
/// </summary>
public interface ICreatureVisibility
{
    /// <summary>Whether the creature detects this player for proximity aggro (vmangos GridNotifiersImpl.h:54-70).</summary>
    bool CanCreatureSee(Unit creature, Player target, out bool alert);
}

public sealed class StealthServices(SpellSystem spells, StealthRegistry registry, StealthOptions options) : ICreatureVisibility
{
    private static readonly ConditionalWeakTable<Map, StealthServices> s_installed = new();

    public SpellSystem Spells { get; } = spells ?? throw new ArgumentNullException(nameof(spells));

    public StealthRegistry Registry { get; } = registry ?? throw new ArgumentNullException(nameof(registry));

    public StealthOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>The services installed for <paramref name="map"/>, or null before the stealth feature attached to it.</summary>
    public static StealthServices? Find(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return s_installed.TryGetValue(map, out StealthServices? services) ? services : null;
    }

    /// <summary>Install (or replace) the services of <paramref name="map"/>.</summary>
    public static void Install(Map map, StealthServices services)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(services);
        s_installed.AddOrUpdate(map, services);
    }

    /// <summary>
    /// Whether <paramref name="creature"/> sees <paramref name="target"/>: always, unless the target is stealthed or invisible. A stealthed player is
    /// seen when the vmangos formula says so for the creature's levels (<see cref="StealthDetection"/>, creature constants: 5/6 yard base
    /// and per level) and the creature has line of sight. A unit that has just stealthed is not seen (NO_DETECT).
    /// <paramref name="alert"/> is true when the target is beyond sight but inside the alert band (visible distance + 5 yards): the creature
    /// notices something (vmangos OnMoveInStealth). The alert behaviour itself (SMSG_AI_REACTION, facing, distract, the 10 s cooldown,
    /// AI/CreatureAI.cpp:349-385) belongs to creature AI and is not implemented here.
    /// </summary>
    public bool CanCreatureSee(Unit creature, Player target, out bool alert)
    {
        ArgumentNullException.ThrowIfNull(creature);
        ArgumentNullException.ThrowIfNull(target);
        alert = false;
        StealthVisibility group = Registry.VisibilityOf(target);
        if (group == StealthVisibility.On)
        {
            return true;
        }

        if (group == StealthVisibility.NoDetect)
        {
            return false;
        }

        // vmangos Unit.cpp:6401-6433: invisibility is checked before stealth detection.
        if (Spells.HasAuraType(target, AuraType.ModInvisibility)
            && !InvisibilityAuras.CanDetect(Spells, creature, target))
        {
            return false;
        }

        if (group == StealthVisibility.Invisibility)
        {
            return true;
        }

        float dx = creature.X - target.X;
        float dy = creature.Y - target.Y;
        float dz = creature.Z - target.Z;
        StealthDetectionResult result = StealthDetection.CanDetectStealthOf(Spells, creature, target, MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)), Options);
        alert = result.Alert;
        if (!result.Detected)
        {
            return false;
        }

        return creature.Map is not { } map || map.Collision.IsWithinLineOfSight(creature, target);
    }
}

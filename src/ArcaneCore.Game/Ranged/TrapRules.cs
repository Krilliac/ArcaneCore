using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.Ranged;

/// <summary>
/// The trap (game object type 6) rules of vmangos GameObject::Update (GameObject.cpp:455-560):
/// the data columns, the trigger radius and the hunter trap target filter
/// (HunterTrapTargetSelectorCheck, GameObject.cpp:274-308).
/// </summary>
public static class TrapRules
{
    /// <summary>gameobject_template.data2 of a trap: the activation radius in yards.</summary>
    public const int RadiusData = 2;

    /// <summary>data3: the spell the trap casts.</summary>
    public const int SpellData = 3;

    /// <summary>data4: charges (&gt; 0 marks a hunter trap and despawns it after that many uses).</summary>
    public const int ChargesData = 4;

    /// <summary>data5: cooldown in seconds (4 when 0).</summary>
    public const int CooldownData = 5;

    /// <summary>data7: arming delay in seconds.</summary>
    public const int StartDelayData = 7;

    /// <summary>The cooldown a trap without its own waits after a trigger (GameObject.cpp:541: 4 s).</summary>
    public const uint DefaultCooldownSeconds = 4;

    /// <summary>The radius of the twelve hunter trap templates in vmangos (a float, the database holds an integer: GameObject.cpp:482-497).</summary>
    public const float HunterTrapRadius = 2.5f;

    /// <summary>The templates vmangos forces to <see cref="HunterTrapRadius"/>: Freezing, Immolation, Frost and Explosive Trap and their ranks.</summary>
    public static IReadOnlySet<uint> HunterTrapEntries { get; } = new HashSet<uint>
    {
        2561, 164638, 164639, 164839, 164872, 164873, 164874, 164875, 164876, 164877, 164879, 164880,
    };

    /// <summary>
    /// The display ids whose triggering plays a custom animation (vmangos GameObject::HasCustomAnim,
    /// GameObject.cpp:2431-2447): the four hunter trap models among them.
    /// </summary>
    public static IReadOnlySet<uint> CustomAnimDisplays { get; } = new HashSet<uint>
    {
        2570, 3071, 3072, 3073, 3074, 4392, 4472, 4491, 6785, 6747, 6871,
    };

    /// <summary>
    /// The activation radius (0 = the trap never scans): data2, except that vmangos forces the hunter
    /// trap templates to 2.5 yd (<see cref="TrapRadiusSource.Vmangos"/>).
    /// </summary>
    public static float TriggerRadius(GameObjectTemplate template, TrapRadiusSource source)
    {
        ArgumentNullException.ThrowIfNull(template);
        float radius = template.GetData(RadiusData);
        if (radius == 0)
        {
            return 0;
        }

        return source == TrapRadiusSource.Vmangos && HunterTrapEntries.Contains(template.Entry) ? HunterTrapRadius : radius;
    }

    /// <summary>
    /// The owner side of HunterTrapTargetSelectorCheck: a player owner's trap ignores player targets
    /// unless the owner is PvP-flagged (free-for-all and duel exceptions are not modelled).
    /// </summary>
    public static bool OwnerMayTrigger(Unit owner, Unit target)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(target);
        return owner is not Player || target is not Player || (owner.UnitFlags & UnitFlags.Pvp) != 0;
    }
}

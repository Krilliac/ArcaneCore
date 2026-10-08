using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The <c>Locomotion</c> configuration section. Every default is the vmangos value (vmangos is the
/// retail reference); a deliberate deviation would be a non-default value of one of these keys.
/// Only the keys whose rule is implemented are listed: a key for an undelivered rule would be a stub
/// (docs/areas/locomotion-foundation.md lists what is not delivered).
/// </summary>
public sealed class LocomotionOptions
{
    public const string SectionName = "Locomotion";

    /// <summary>
    /// How long the client has to acknowledge a server-ordered movement change before the server
    /// enforces it (vmangos Movement.PendingAckResponseTime, World.cpp:985, default 4000 ms; the wait is
    /// multiplied by 5 while the player is being teleported, Unit.cpp:6633).
    /// </summary>
    public uint PendingAckResponseTimeMs { get; set; } = 4000;

    /// <summary>Fall damage multiplier (vmangos Rate.Damage.Fall, World.cpp:533, default 1; setConfigPos: a negative value becomes 1).</summary>
    public float RateDamageFall { get; set; } = 1.0f;

    /// <summary>Seconds of fatigue in deep water before the first pulse (vmangos MirrorTimer.Fatigue.Max, World.cpp:814, default 60).</summary>
    public uint MirrorTimerFatigueMaxSec { get; set; } = 60;

    /// <summary>Seconds of breath under water, times the water breathing multiplier (vmangos MirrorTimer.Breath.Max, World.cpp:815, default 60).</summary>
    public uint MirrorTimerBreathMaxSec { get; set; } = 60;

    /// <summary>Seconds in lava or slime before the first pulse (vmangos MirrorTimer.Environmental.Max, World.cpp:816, default 1).</summary>
    public uint MirrorTimerEnvironmentalMaxSec { get; set; } = 1;

    /// <summary>Lowest lava damage per pulse (vmangos EnvironmentalDamage.Min, World.cpp:817, default 605).</summary>
    public uint EnvironmentalDamageMin { get; set; } = 605;

    /// <summary>Highest lava damage per pulse (vmangos EnvironmentalDamage.Max, World.cpp:818, default 610, at least the minimum).</summary>
    public uint EnvironmentalDamageMax { get; set; } = 610;

    /// <summary>
    /// Deliberate deviation, off by default: hurt in slime like in lava. vmangos (Player.cpp:1030-1040) and mangos-classic
    /// (Player.cpp:1305-1311, "FIXME ... Undercity") damage only in magma although both define DAMAGE_SLIME; whether retail 1.12 hurt
    /// in slime cannot be proven from the references. When on, a slime pulse deals the same 605-610 as lava.
    /// </summary>
    public bool SlimeDamage { get; set; }

    /// <summary>
    /// Speed multiplier of a player whose death state is CORPSE, outside battlegrounds (vmangos Death.Ghost.RunSpeed.World,
    /// World.cpp:777, setConfigMinMax 0.1 to 10, default 1). Read literally as vmangos does (Unit.cpp:7044); at 1 it does nothing.
    /// </summary>
    public float GhostRunSpeedWorld { get; set; } = 1.0f;

    /// <summary>The same inside a battleground (vmangos Death.Ghost.RunSpeed.BG, World.cpp:778, default 1).</summary>
    public float GhostRunSpeedBattleground { get; set; } = 1.0f;

    /// <summary>
    /// Non-retail when not 1 (default 1, retail): multiplies every movement speed of every player (run, run back, swim, swim back, walk), on top of
    /// the per-type rates below. Not a vmangos key: the MaNGOS Zero fork's Movement.PlayerSpeedRate (feature/movement-enhancements,
    /// WorldConfig.cpp, percent 10 to 1000, applied in the player block of Unit::UpdateSpeed, UnitSpeed.cpp:162-168), here a multiplier clamped
    /// to 0.1 to 10. Applied where a player's speed is set (<c>UnitSpeed.SetRate</c>), so every speed change, force-speed packet and the
    /// server's own movement use the result. Live: <c>.reload config</c> and <c>.movement set</c> re-send the speeds of every online player.
    /// </summary>
    public float PlayerSpeedRate { get; set; } = 1.0f;

    /// <summary>Non-retail when not 1 (default 1): player run speed multiplier (the fork's Movement.RunSpeedRate, which also covers run back; 0.1 to 10; live).</summary>
    public float PlayerRunSpeedRate { get; set; } = 1.0f;

    /// <summary>Non-retail when not 1 (default 1): player run-back speed multiplier (the fork's Movement.RunSpeedRate on MOVE_RUN_BACK; 0.1 to 10; live).</summary>
    public float PlayerRunBackSpeedRate { get; set; } = 1.0f;

    /// <summary>Non-retail when not 1 (default 1): player swim speed multiplier (the fork's Movement.SwimSpeedRate, which also covers swim back; 0.1 to 10; live).</summary>
    public float PlayerSwimSpeedRate { get; set; } = 1.0f;

    /// <summary>Non-retail when not 1 (default 1): player swim-back speed multiplier (the fork's Movement.SwimSpeedRate on MOVE_SWIM_BACK; 0.1 to 10; live).</summary>
    public float PlayerSwimBackSpeedRate { get; set; } = 1.0f;

    /// <summary>Non-retail when not 1 (default 1): player walk speed multiplier (the fork's Movement.WalkSpeedRate; 0.1 to 10; live).</summary>
    public float PlayerWalkSpeedRate { get; set; } = 1.0f;

    /// <summary>
    /// Non-retail when not 1 (default 1): player turn rate multiplier (0.1 to 10; live). Neither vmangos nor the fork has one; the base is vmangos
    /// baseMoveSpeed[MOVE_TURN_RATE] = 3.141594 rad/s (Unit.cpp:67-74). <see cref="PlayerSpeedRate"/> does not apply to it. A change reaches the
    /// client as SMSG_FORCE_TURN_RATE_CHANGE.
    /// </summary>
    public float PlayerTurnRate { get; set; } = 1.0f;

    /// <summary>The lowest player speed rate (the fork's 10 percent).</summary>
    public const float MinSpeedRate = 0.1f;

    /// <summary>The highest player speed rate (the fork's 1000 percent).</summary>
    public const float MaxSpeedRate = 10.0f;

    /// <summary>The player speed rates these options give: <see cref="PlayerSpeedRate"/> times each per-type rate, the turn rate alone.</summary>
    public PlayerSpeedRates SpeedRates => new(
        Walk: PlayerSpeedRate * PlayerWalkSpeedRate,
        Run: PlayerSpeedRate * PlayerRunSpeedRate,
        RunBack: PlayerSpeedRate * PlayerRunBackSpeedRate,
        Swim: PlayerSpeedRate * PlayerSwimSpeedRate,
        SwimBack: PlayerSpeedRate * PlayerSwimBackSpeedRate,
        Turn: PlayerTurnRate);

    private static float ClampRate(float value, string name, List<string> changed)
    {
        float clamped = float.IsNaN(value) ? 1.0f : Math.Clamp(value, MinSpeedRate, MaxSpeedRate);
        if (clamped != value)
        {
            changed.Add(name);
        }

        return clamped;
    }

    /// <summary>Apply vmangos' setConfigPos fallback to the values that must not be negative; returns the names whose value was reset or clamped.</summary>
    public IReadOnlyList<string> Normalize()
    {
        List<string> reset = [];
        if (!(RateDamageFall >= 0.0f))
        {
            RateDamageFall = 1.0f;
            reset.Add(nameof(RateDamageFall));
        }

        // setConfigMin(EnvironmentalDamage.Max, 610, EnvironmentalDamage.Min): the maximum is at least the minimum (World.cpp:818).
        if (EnvironmentalDamageMax < EnvironmentalDamageMin)
        {
            EnvironmentalDamageMax = EnvironmentalDamageMin;
            reset.Add(nameof(EnvironmentalDamageMax));
        }

        // setConfigMinMax(..., 1.0f, 0.1f, 10.0f): a value outside the range is clamped (a NaN is not in range either).
        GhostRunSpeedWorld = ClampRate(GhostRunSpeedWorld, nameof(GhostRunSpeedWorld), reset);
        GhostRunSpeedBattleground = ClampRate(GhostRunSpeedBattleground, nameof(GhostRunSpeedBattleground), reset);

        // The fork reads its speed rates with setConfigMinMax(..., 100, 10, 1000) percent (WorldConfig.cpp): 0.1 to 10 here.
        PlayerSpeedRate = ClampRate(PlayerSpeedRate, nameof(PlayerSpeedRate), reset);
        PlayerRunSpeedRate = ClampRate(PlayerRunSpeedRate, nameof(PlayerRunSpeedRate), reset);
        PlayerRunBackSpeedRate = ClampRate(PlayerRunBackSpeedRate, nameof(PlayerRunBackSpeedRate), reset);
        PlayerSwimSpeedRate = ClampRate(PlayerSwimSpeedRate, nameof(PlayerSwimSpeedRate), reset);
        PlayerSwimBackSpeedRate = ClampRate(PlayerSwimBackSpeedRate, nameof(PlayerSwimBackSpeedRate), reset);
        PlayerWalkSpeedRate = ClampRate(PlayerWalkSpeedRate, nameof(PlayerWalkSpeedRate), reset);
        PlayerTurnRate = ClampRate(PlayerTurnRate, nameof(PlayerTurnRate), reset);
        return reset;
    }
}

/// <summary>
/// The per-world locomotion seam: the options every locomotion rule reads and the probe that answers
/// whether a player is being teleported (the teleport state machine lives in the world daemon). Registered
/// per <see cref="WorldRuntime"/> with the same pattern as <c>DeathHooks</c>; the daemon's
/// <c>LocomotionFeature</c> registers the configured instance before the world thread starts.
/// </summary>
public sealed class LocomotionEnvironment
{
    private static readonly ConditionalWeakTable<WorldRuntime, LocomotionEnvironment> s_registered = new();

    public LocomotionEnvironment(LocomotionOptions options, Func<Player, bool>? isBeingTeleported = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        IsBeingTeleported = isBeingTeleported ?? (static _ => false);
    }

    /// <summary>Retail defaults and no teleport probe; used when nothing is registered.</summary>
    public static LocomotionEnvironment Default { get; } = new(new LocomotionOptions());

    public LocomotionOptions Options { get; }

    /// <summary>Whether the player is between a teleport order and its acknowledgement (vmangos Player::IsBeingTeleported).</summary>
    public Func<Player, bool> IsBeingTeleported { get; }

    /// <summary>Use <paramref name="environment"/> for <paramref name="world"/> (call before the world thread starts).</summary>
    public static void Register(WorldRuntime world, LocomotionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(environment);
        s_registered.AddOrUpdate(world, environment);
    }

    private static readonly ConditionalWeakTable<WorldRuntime, IEnvironmentalDamageMitigation> s_mitigations = new();

    /// <summary>
    /// Use <paramref name="mitigation"/> for the environmental damage of <paramref name="world"/> (the spell combat rules
    /// feature registers it; independent of the order the features attach in).
    /// </summary>
    public static void RegisterMitigation(WorldRuntime world, IEnvironmentalDamageMitigation mitigation)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(mitigation);
        s_mitigations.AddOrUpdate(world, mitigation);
    }

    private static readonly ConditionalWeakTable<WorldRuntime, IEnvironmentSpellBridge> s_spellBridges = new();

    /// <summary>Use <paramref name="bridge"/> for the aura and channel removal of the liquid rules in <paramref name="world"/> (the world daemon's spell system).</summary>
    public static void RegisterSpellBridge(WorldRuntime world, IEnvironmentSpellBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(bridge);
        s_spellBridges.AddOrUpdate(world, bridge);
    }

    /// <summary>The spell bridge of the world <paramref name="map"/> belongs to, or null when none is registered.</summary>
    public static IEnvironmentSpellBridge? SpellBridgeFor(Map? map)
        => map?.FindUpdater<MapLocomotion>()?.World is { } world && s_spellBridges.TryGetValue(world, out IEnvironmentSpellBridge? bridge) ? bridge : null;

    private static readonly ConditionalWeakTable<WorldRuntime, IMountDisplaySource> s_mountDisplays = new();

    /// <summary>Use <paramref name="source"/> to resolve mount spells' creature entries to display ids in <paramref name="world"/> (the world daemon's creature data).</summary>
    public static void RegisterMountDisplays(WorldRuntime world, IMountDisplaySource source)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(source);
        s_mountDisplays.AddOrUpdate(world, source);
    }

    /// <summary>The mount display source of the world <paramref name="map"/> belongs to, or null when none is registered.</summary>
    public static IMountDisplaySource? MountDisplaysFor(Map? map)
        => map?.FindUpdater<MapLocomotion>()?.World is { } world && s_mountDisplays.TryGetValue(world, out IMountDisplaySource? source) ? source : null;

    private static readonly ConditionalWeakTable<WorldRuntime, IFallDamageModifiers> s_fallModifiers = new();

    /// <summary>Use <paramref name="modifiers"/> for the fall damage of <paramref name="world"/> (the spell combat rules feature registers it).</summary>
    public static void RegisterFallModifiers(WorldRuntime world, IFallDamageModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(modifiers);
        s_fallModifiers.AddOrUpdate(world, modifiers);
    }

    /// <summary>The registered fall modifiers, or the pass-through ones.</summary>
    public static IFallDamageModifiers FallModifiersFor(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_fallModifiers.TryGetValue(world, out IFallDamageModifiers? modifiers) ? modifiers : NoFallDamageModifiers.Instance;
    }

    /// <summary>The registered mitigation, or the pass-through one.</summary>
    public static IEnvironmentalDamageMitigation MitigationFor(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_mitigations.TryGetValue(world, out IEnvironmentalDamageMitigation? mitigation) ? mitigation : NoEnvironmentalMitigation.Instance;
    }

    /// <summary>
    /// The environment of the world a map belongs to (rules that run from aura handlers have the unit but not the
    /// world); <see cref="Default"/> when the map is null or has no locomotion updater.
    /// </summary>
    public static LocomotionEnvironment For(Map? map) => map?.FindUpdater<MapLocomotion>()?.Environment ?? Default;

    /// <summary>
    /// The options of the environment registered for <paramref name="world"/>, or null when none is registered (the shared
    /// <see cref="Default"/> must never be changed by a reload).
    /// </summary>
    public static LocomotionOptions? RegisteredOptions(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out LocomotionEnvironment? environment) ? environment.Options : null;
    }

    /// <summary>The environment registered for <paramref name="world"/>, or <see cref="Default"/>.</summary>
    public static LocomotionEnvironment For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out LocomotionEnvironment? environment) ? environment : Default;
    }
}

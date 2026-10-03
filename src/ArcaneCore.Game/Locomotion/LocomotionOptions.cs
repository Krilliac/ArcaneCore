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

    /// <summary>Apply vmangos' setConfigPos fallback to the values that must not be negative; returns the names that were reset.</summary>
    public IReadOnlyList<string> Normalize()
    {
        List<string> reset = [];
        if (!(RateDamageFall >= 0.0f))
        {
            RateDamageFall = 1.0f;
            reset.Add(nameof(RateDamageFall));
        }

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

    /// <summary>The environment registered for <paramref name="world"/>, or <see cref="Default"/>.</summary>
    public static LocomotionEnvironment For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out LocomotionEnvironment? environment) ? environment : Default;
    }
}

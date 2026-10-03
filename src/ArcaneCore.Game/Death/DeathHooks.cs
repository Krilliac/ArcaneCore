using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Death;

/// <summary>
/// The per-world death seam: the options and the clock every death rule reads. It is
/// registered per <see cref="WorldRuntime"/> with the same pattern as <c>CombatHooks</c>
/// (<see cref="Register"/>, <see cref="TryRegister"/>, <see cref="For"/>); the world daemon's
/// <c>DeathFeature</c> registers the configured instance before the world thread starts.
/// </summary>
public sealed class DeathHooks
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldRuntime, DeathHooks> s_registered = new();

    public DeathHooks(DeathOptions options, DeathClock clock)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Retail defaults and the system clock; used when nothing is registered.</summary>
    public static DeathHooks Default { get; } = new(new DeathOptions(), DeathClock.System);

    public DeathOptions Options { get; }

    public DeathClock Clock { get; }

    /// <summary>Use <paramref name="hooks"/> for every map of <paramref name="world"/> (call before the world thread starts).</summary>
    public static void Register(WorldRuntime world, DeathHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        s_registered.AddOrUpdate(world, hooks);
    }

    /// <summary>Register only when nothing is registered yet; the first registration wins. <see cref="Default"/> is never registered.</summary>
    public static bool TryRegister(WorldRuntime world, DeathHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        return !ReferenceEquals(hooks, Default) && s_registered.TryAdd(world, hooks);
    }

    /// <summary>The hooks registered for <paramref name="world"/>, or <see cref="Default"/>.</summary>
    public static DeathHooks For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out DeathHooks? hooks) ? hooks : Default;
    }
}

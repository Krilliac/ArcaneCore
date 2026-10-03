using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// The per-world honor seam: the options and the clock every honor rule reads. Registered per
/// <see cref="WorldRuntime"/> with the same pattern as <c>DeathHooks</c>; the daemon's honor feature registers the
/// configured instance before the world thread starts.
/// </summary>
public sealed class HonorHooks
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldRuntime, HonorHooks> s_registered = new();

    public HonorHooks(HonorOptions options, HonorClock clock)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Retail defaults and the system clock; used when nothing is registered.</summary>
    public static HonorHooks Default { get; } = new(new HonorOptions(), HonorClock.System);

    public HonorOptions Options { get; }

    public HonorClock Clock { get; }

    /// <summary>
    /// A player's internal honor rank (0..18) for the rules that sit below the honor owner, such as the WorldDefense channel
    /// (vmangos Channel.cpp:636-648, 670). Set by the daemon's honor feature; null means every player is unranked.
    /// </summary>
    public Func<Entities.Player, byte>? InternalRank { get; set; }

    /// <summary>Use <paramref name="hooks"/> for <paramref name="world"/> (call before the world thread starts).</summary>
    public static void Register(WorldRuntime world, HonorHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        s_registered.AddOrUpdate(world, hooks);
    }

    /// <summary>Register only when nothing is registered yet; the first registration wins.</summary>
    public static bool TryRegister(WorldRuntime world, HonorHooks hooks)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(hooks);
        return !ReferenceEquals(hooks, Default) && s_registered.TryAdd(world, hooks);
    }

    /// <summary>The hooks registered for <paramref name="world"/>, or <see cref="Default"/>.</summary>
    public static HonorHooks For(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return s_registered.TryGetValue(world, out HonorHooks? hooks) ? hooks : Default;
    }
}

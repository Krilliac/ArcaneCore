using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// A rule that looks at the movement block a client sends, at the two places vmangos' movement handler does
/// (MovementHandler.cpp:290-390 HandleMovementOpcodes and HandleMoverRelocation :1062-1170): fall tracking and
/// flag correction before the block is stored, liquid and void checks after.
/// <para>
/// Implement it in this assembly on a class with a parameterless constructor and mark it
/// <see cref="MovementObserverAttribute"/>; <see cref="MovementObservers"/> discovers it by reflection
/// (docs/integration/seams.md: discovery instead of a registration list). Instances are shared by every player,
/// so they hold no per-player state: keep that in <c>unit.Locomotion</c>.
/// </para>
/// </summary>
public interface IClientMovementObserver
{
    /// <summary>
    /// Runs after the packet was validated and before it is stored. <paramref name="previous"/> is the movement
    /// last stored for the player (HandleFall reads the previous flags); <paramref name="incoming"/> may be
    /// corrected in place (Object.cpp:153 MovementInfo::CorrectData).
    /// </summary>
    void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
    }

    /// <summary>Runs after the block was stored and the player moved to its position.</summary>
    void AfterApply(MovementObserverContext context, in MovementInfo previous)
    {
    }
}

/// <summary>What an observer needs to know about the movement being processed.</summary>
/// <param name="Player">The player whose client sent the block.</param>
/// <param name="World">The world (options, clock).</param>
/// <param name="Opcode">The movement opcode (or the ack opcode when the block came with an acknowledgement).</param>
public readonly record struct MovementObserverContext(Player Player, WorldRuntime World, WorldOpcode Opcode);

/// <summary>Marks an <see cref="IClientMovementObserver"/> for discovery. Lower <see cref="Order"/> runs first; ties by full type name.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MovementObserverAttribute : Attribute
{
    public int Order { get; init; }
}

/// <summary>Discovery and dispatch of the <see cref="IClientMovementObserver"/> classes of this assembly.</summary>
public static class MovementObservers
{
    private static readonly (IClientMovementObserver Observer, int Order, string Name)[] s_discovered = Discover();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldRuntime, WorldObservers> s_perWorld = new();

    /// <summary>The discovered observer types in run order.</summary>
    public static IReadOnlyList<Type> Types { get; } = s_discovered.Select(o => o.Observer.GetType()).ToArray();

    /// <summary>
    /// Add an observer that lives outside this assembly (the world daemon's, a test's) to one world; it runs among the
    /// discovered ones by <paramref name="order"/> (ties: discovered first, then in registration order). Call before
    /// the world thread starts.
    /// </summary>
    public static void Register(WorldRuntime world, IClientMovementObserver observer, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(observer);
        s_perWorld.GetValue(world, static _ => new WorldObservers()).Add(observer, order);
    }

    private static IReadOnlyList<IClientMovementObserver> For(WorldRuntime world)
        => s_perWorld.TryGetValue(world, out WorldObservers? extra) ? extra.Merged() : DiscoveredOnly;

    private static readonly IClientMovementObserver[] DiscoveredOnly = s_discovered.Select(o => o.Observer).ToArray();

    /// <summary>Run every observer's <see cref="IClientMovementObserver.BeforeApply"/> (a throwing observer is logged and the rest still run).</summary>
    public static void Before(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming, ILogger? logger = null)
        => Before(For(context.World), context, previous, ref incoming, logger);

    /// <summary>Run every observer's <see cref="IClientMovementObserver.AfterApply"/>.</summary>
    public static void After(MovementObserverContext context, in MovementInfo previous, ILogger? logger = null)
        => After(For(context.World), context, previous, logger);

    private sealed class WorldObservers
    {
        private readonly List<(IClientMovementObserver Observer, int Order, int Sequence)> _added = [];
        private IClientMovementObserver[]? _merged;

        public void Add(IClientMovementObserver observer, int order)
        {
            _added.Add((observer, order, _added.Count));
            _merged = null;
        }

        public IReadOnlyList<IClientMovementObserver> Merged()
            => _merged ??= s_discovered.Select(d => (d.Observer, d.Order, Sequence: -1))
                .Concat(_added)
                .OrderBy(o => o.Order)
                .ThenBy(o => o.Sequence)
                .Select(o => o.Observer)
                .ToArray();
    }
    /// <summary>Dispatch to an explicit list (the discovered list in production; tests pass their own).</summary>
    public static void Before(IReadOnlyList<IClientMovementObserver> observers, MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming, ILogger? logger = null)
    {
        foreach (IClientMovementObserver observer in observers)
        {
            try
            {
                observer.BeforeApply(context, in previous, ref incoming);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "movement observer {Observer} failed before applying {Opcode}", observer.GetType().Name, context.Opcode);
            }
        }
    }

    public static void After(IReadOnlyList<IClientMovementObserver> observers, MovementObserverContext context, in MovementInfo previous, ILogger? logger = null)
    {
        foreach (IClientMovementObserver observer in observers)
        {
            try
            {
                observer.AfterApply(context, in previous);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "movement observer {Observer} failed after applying {Opcode}", observer.GetType().Name, context.Opcode);
            }
        }
    }

    /// <summary>
    /// Every marked type. A marked type that is not a concrete <see cref="IClientMovementObserver"/> or has no
    /// parameterless constructor fails here, at first use, instead of being skipped (fail closed).
    /// </summary>
    private static (IClientMovementObserver, int, string)[] Discover()
    {
        var found = new List<(IClientMovementObserver Observer, int Order, string Name)>();
        foreach (Type type in typeof(MovementObservers).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<MovementObserverAttribute>() is not { } attribute)
            {
                continue;
            }

            if (type.IsAbstract || !typeof(IClientMovementObserver).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"{type.FullName} is marked [MovementObserver] but is not a concrete IClientMovementObserver");
            }

            ConstructorInfo constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
                ?? throw new InvalidOperationException($"{type.FullName} is marked [MovementObserver] but has no parameterless constructor");
            found.Add(((IClientMovementObserver)constructor.Invoke(null), attribute.Order, type.FullName ?? type.Name));
        }

        return found
            .OrderBy(f => f.Order)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

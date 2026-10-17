using System.Reflection;
using ArcaneCore.Kernel.Reflection;

namespace ArcaneCore.Game.Creatures;

/// <summary>The EventAI handlers declared in a set of assemblies (what a module hands to <see cref="EventAiRegistry.CommitSwap"/>).</summary>
public sealed record EventAiHandlerSet(IReadOnlyList<EventAiEventHandler> Events, IReadOnlyList<EventAiActionHandler> Actions);

/// <summary>
/// The EventAI event and action handlers, discovered by reflection over an assembly: every non-abstract
/// <see cref="EventAiEventHandler"/> / <see cref="EventAiActionHandler"/> with a parameterless constructor is
/// registered under its type id. Two handlers for one id fail at startup (fail closed). Adding a handler is
/// adding a class; no existing file changes.
/// <para>
/// Hot swap (the hot module lane): the handlers live in an immutable table that <see cref="CommitSwap"/> replaces in one reference write.
/// This registry object is the holder: an <see cref="EventAiEngine"/> keeps the registry it was built with (<see cref="Default"/> unless a
/// test passes another), never a copy of the table, so a commit on <see cref="Default"/> reaches every running EventAI creature. Action
/// handlers are looked up per action, so an action swap applies from the next action run. Event handlers are bound to a creature's
/// <see cref="EventAiHolder"/> rows when the engine builds them (spawn, respawn), because each row's timer and phase state belongs to the
/// handler that started it: an event swap applies to creatures built afterwards, and a creature built before keeps its handler until it
/// respawns (<see cref="IsCurrent"/> tells whether a bound handler is still the current one). Handlers are stateless (the per-row state is
/// the holder), so a swap between two updates leaves nothing half-run.
/// </para>
/// <para>
/// Handlers found by <see cref="Discover(Assembly)"/> are built in: a module swap can add handlers for type ids without one and replace or
/// remove its own, but never override or remove a built-in one (fail closed).
/// </para>
/// </summary>
public sealed class EventAiRegistry
{
    // One immutable table; a swap publishes a new one. Reads take the reference once, so a lookup never sees half a swap.
    private sealed record Table(
        Dictionary<byte, EventAiEventHandler> Events,
        Dictionary<byte, EventAiActionHandler> Actions,
        HashSet<byte> ModuleEvents,
        HashSet<byte> ModuleActions);

    private readonly Lock _writeLock = new();
    private volatile Table _table;
    private long _generation;

    private EventAiRegistry(Dictionary<byte, EventAiEventHandler> events, Dictionary<byte, EventAiActionHandler> actions)
    {
        _table = new Table(events, actions, [], []);
    }

    /// <summary>The code set whose handlers are built in: the Game assembly (no pack is configured at startup).</summary>
    public static CodeAssemblies StartupAssemblies { get; } = CodeAssemblies.Of(typeof(EventAiRegistry).Assembly);

    /// <summary>The handlers of the startup code set (<see cref="StartupAssemblies"/>); module swaps go through this instance so every EventAI creature sees them.</summary>
    public static EventAiRegistry Default { get; } = Discover(StartupAssemblies.Assemblies);

    public IReadOnlyCollection<byte> EventTypes => _table.Events.Keys;

    public IReadOnlyCollection<byte> ActionTypes => _table.Actions.Keys;

    /// <summary>The event type ids whose handler came from a module swap (not built in).</summary>
    public IReadOnlyCollection<byte> ModuleEventTypes => _table.ModuleEvents;

    /// <summary>The action type ids whose handler came from a module swap (not built in).</summary>
    public IReadOnlyCollection<byte> ModuleActionTypes => _table.ModuleActions;

    /// <summary>Bumped by every <see cref="CommitSwap"/>.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    public EventAiEventHandler? FindEvent(byte type) => _table.Events.GetValueOrDefault(type);

    public EventAiActionHandler? FindAction(byte type) => _table.Actions.GetValueOrDefault(type);

    /// <summary>
    /// Whether <paramref name="handler"/> is the current handler of its event type: false once a swap replaced or removed it (retired handler
    /// code still bound to the holders of creatures built before the swap).
    /// </summary>
    public bool IsCurrent(EventAiEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ReferenceEquals(FindEvent(handler.EventType), handler);
    }

    /// <summary>Whether the handler of event type <paramref name="type"/> is built in (a module cannot override or remove it).</summary>
    public bool IsBuiltInEvent(byte type)
    {
        Table table = _table;
        return table.Events.ContainsKey(type) && !table.ModuleEvents.Contains(type);
    }

    /// <summary>Whether the handler of action type <paramref name="type"/> is built in (a module cannot override or remove it).</summary>
    public bool IsBuiltInAction(byte type)
    {
        Table table = _table;
        return table.Actions.ContainsKey(type) && !table.ModuleActions.Contains(type);
    }

    /// <summary>
    /// Whether <see cref="CommitSwap"/> would work, checked without changing anything: every type to remove has a module handler, every type to
    /// add is listed once and is free once the removals are done, and no built-in handler is overridden or removed. Null when it would; else why not.
    /// </summary>
    public string? ValidateSwap(
        IReadOnlyCollection<byte> removeEvents, IReadOnlyCollection<byte> removeActions, IEnumerable<byte> addEvents, IEnumerable<byte> addActions)
    {
        ArgumentNullException.ThrowIfNull(removeEvents);
        ArgumentNullException.ThrowIfNull(removeActions);
        ArgumentNullException.ThrowIfNull(addEvents);
        ArgumentNullException.ThrowIfNull(addActions);
        Table table = _table;
        return Validate("event", table.Events.ContainsKey, table.ModuleEvents, removeEvents, addEvents)
            ?? Validate("action", table.Actions.ContainsKey, table.ModuleActions, removeActions, addActions);
    }

    /// <summary>
    /// Remove the module handlers of <paramref name="removeEvents"/> / <paramref name="removeActions"/> and add the given handlers in one step
    /// (the hot module lane; call at a tick boundary on the world thread, after <see cref="ValidateSwap"/>). Re-validates and throws
    /// <see cref="InvalidOperationException"/> without changing anything when the swap is not valid. Every EventAI creature uses the new table
    /// from its next update.
    /// </summary>
    public void CommitSwap(
        IReadOnlyCollection<byte> removeEvents,
        IReadOnlyCollection<byte> removeActions,
        IReadOnlyCollection<EventAiEventHandler> addEvents,
        IReadOnlyCollection<EventAiActionHandler> addActions)
    {
        ArgumentNullException.ThrowIfNull(removeEvents);
        ArgumentNullException.ThrowIfNull(removeActions);
        ArgumentNullException.ThrowIfNull(addEvents);
        ArgumentNullException.ThrowIfNull(addActions);
        if (addEvents.Any(h => h is null) || addActions.Any(h => h is null))
        {
            throw new ArgumentException("a null EventAI handler cannot be added");
        }

        lock (_writeLock)
        {
            Table current = _table;
            if ((Validate("event", current.Events.ContainsKey, current.ModuleEvents, removeEvents, addEvents.Select(h => h.EventType))
                ?? Validate("action", current.Actions.ContainsKey, current.ModuleActions, removeActions, addActions.Select(h => h.ActionType))) is { } error)
            {
                throw new InvalidOperationException(error);
            }

            var events = new Dictionary<byte, EventAiEventHandler>(current.Events);
            var actions = new Dictionary<byte, EventAiActionHandler>(current.Actions);
            var moduleEvents = new HashSet<byte>(current.ModuleEvents);
            var moduleActions = new HashSet<byte>(current.ModuleActions);
            foreach (byte type in removeEvents)
            {
                events.Remove(type);
                moduleEvents.Remove(type);
            }

            foreach (byte type in removeActions)
            {
                actions.Remove(type);
                moduleActions.Remove(type);
            }

            foreach (EventAiEventHandler handler in addEvents)
            {
                events[handler.EventType] = handler;
                moduleEvents.Add(handler.EventType);
            }

            foreach (EventAiActionHandler handler in addActions)
            {
                actions[handler.ActionType] = handler;
                moduleActions.Add(handler.ActionType);
            }

            _table = new Table(events, actions, moduleEvents, moduleActions);
            Interlocked.Increment(ref _generation);
        }
    }

    /// <summary>Build a registry from the handlers declared in <paramref name="assembly"/>.</summary>
    public static EventAiRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Discover([assembly]);
    }

    /// <summary>Build a registry from the handlers declared in <paramref name="assemblies"/>; two handlers for one id across them fail (fail closed).</summary>
    public static EventAiRegistry Discover(IEnumerable<Assembly> assemblies)
    {
        EventAiHandlerSet set = DiscoverHandlers(assemblies);
        return new EventAiRegistry(set.Events.ToDictionary(h => h.EventType), set.Actions.ToDictionary(h => h.ActionType));
    }

    /// <summary>
    /// The handlers declared in <paramref name="assemblies"/>, one instance each (what a module hands to <see cref="CommitSwap"/>). Two handlers
    /// for one type id throw <see cref="InvalidOperationException"/>.
    /// </summary>
    public static EventAiHandlerSet DiscoverHandlers(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var events = new Dictionary<byte, EventAiEventHandler>();
        var actions = new Dictionary<byte, EventAiActionHandler>();
        foreach (Assembly assembly in assemblies.Distinct())
        {
            ArgumentNullException.ThrowIfNull(assembly);
            foreach (Type type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (typeof(EventAiEventHandler).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    var handler = (EventAiEventHandler)Activator.CreateInstance(type)!;
                    if (!events.TryAdd(handler.EventType, handler))
                    {
                        throw new InvalidOperationException($"EventAI event type {handler.EventType} is handled twice ({events[handler.EventType].GetType().Name}, {type.Name})");
                    }
                }
                else if (typeof(EventAiActionHandler).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    var handler = (EventAiActionHandler)Activator.CreateInstance(type)!;
                    if (!actions.TryAdd(handler.ActionType, handler))
                    {
                        throw new InvalidOperationException($"EventAI action type {handler.ActionType} is handled twice ({actions[handler.ActionType].GetType().Name}, {type.Name})");
                    }
                }
            }
        }

        return new EventAiHandlerSet([.. events.Values], [.. actions.Values]);
    }

    private static string? Validate(
        string kind, Func<byte, bool> present, HashSet<byte> module, IReadOnlyCollection<byte> remove, IEnumerable<byte> add)
    {
        foreach (byte type in remove)
        {
            if (!present(type))
            {
                return $"EventAI {kind} type {type} has no handler to remove";
            }

            if (!module.Contains(type))
            {
                return $"EventAI {kind} type {type}'s handler is built in; a module cannot remove it";
            }
        }

        var seen = new HashSet<byte>();
        foreach (byte type in add)
        {
            if (!seen.Add(type))
            {
                return $"EventAI {kind} type {type} is added twice";
            }

            if (present(type) && !remove.Contains(type))
            {
                return module.Contains(type)
                    ? $"EventAI {kind} type {type} already has a module handler (remove it in the same swap to replace it)"
                    : $"EventAI {kind} type {type}'s handler is built in; a module cannot override it";
            }
        }

        return null;
    }
}

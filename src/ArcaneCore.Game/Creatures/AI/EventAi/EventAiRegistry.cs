using System.Reflection;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The EventAI event and action handlers, discovered by reflection over an assembly: every non-abstract
/// <see cref="EventAiEventHandler"/> / <see cref="EventAiActionHandler"/> with a parameterless constructor is
/// registered under its type id. Two handlers for one id fail at startup (fail closed). Adding a handler is
/// adding a class; no existing file changes.
/// </summary>
public sealed class EventAiRegistry
{
    private readonly Dictionary<byte, EventAiEventHandler> _events;
    private readonly Dictionary<byte, EventAiActionHandler> _actions;

    private EventAiRegistry(Dictionary<byte, EventAiEventHandler> events, Dictionary<byte, EventAiActionHandler> actions)
    {
        _events = events;
        _actions = actions;
    }

    /// <summary>The handlers of the Game assembly.</summary>
    public static EventAiRegistry Default { get; } = Discover(typeof(EventAiRegistry).Assembly);

    public IReadOnlyCollection<byte> EventTypes => _events.Keys;

    public IReadOnlyCollection<byte> ActionTypes => _actions.Keys;

    public EventAiEventHandler? FindEvent(byte type) => _events.GetValueOrDefault(type);

    public EventAiActionHandler? FindAction(byte type) => _actions.GetValueOrDefault(type);

    /// <summary>Build a registry from the handlers declared in <paramref name="assembly"/>.</summary>
    public static EventAiRegistry Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var events = new Dictionary<byte, EventAiEventHandler>();
        var actions = new Dictionary<byte, EventAiActionHandler>();
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

        return new EventAiRegistry(events, actions);
    }
}

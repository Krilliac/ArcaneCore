using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Atlas;

/// <summary>A point a tool clicked on: the map, instance and coordinates the action channel carries (validated finite and inside the map).</summary>
/// <param name="Map">The map id.</param>
/// <param name="Instance">The instance id, 0 for a continent.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="O">Orientation in radians.</param>
public readonly record struct GmClickPoint(uint Map, uint Instance, float X, float Y, float Z, float O);

/// <summary>
/// What a tool clicked, attached to the <see cref="CommandContext"/> a remote invoker runs a command with (docs/areas/atlas-actions.md,
/// "The click context"): who the actor is, the units it selected and the point it clicked. A command that reads the invoker's own selection
/// (<see cref="Core.GmSelectedUnit"/> and the <c>.atlas</c> leaves) finds it here, because a console-like invoker has no character to select with.
/// <para>
/// Ownership and lifetime: the context is attached to the <see cref="CommandContext"/> <em>object</em> through a
/// <see cref="ConditionalWeakTable{TKey, TValue}"/> (no field on <c>CommandContext</c>) and lives exactly as long as it. An
/// <c>AsyncLocal</c> would not do: <c>WorldRuntime.Post</c> enqueues an <see cref="Action"/> with no execution-context capture, so a command
/// whose work runs inside a deferral would no longer see the flow that set it. Instances are immutable after construction; <see cref="Of"/>
/// and <see cref="Attach"/> are thread-safe. Unit lookups (<see cref="FindUnit"/>) are world-thread only.
/// </para>
/// </summary>
public sealed class GmActionContext
{
    private static readonly ConditionalWeakTable<CommandContext, GmActionContext> Attached = new();

    /// <summary>The invoker name of the actor (<c>atlas-local:...</c>, <c>atlas-web:ACCOUNT</c>): what the audit shows.</summary>
    public required string ActorName { get; init; }

    /// <summary>The actor's account id: a web actor's ticket account, the local actor's configured <c>LocalAccountId</c>; null when it has none.</summary>
    public int? ActorAccountId { get; init; }

    /// <summary>Whether the actor came in over the public listener (a web connection).</summary>
    public bool IsPublic { get; init; }

    /// <summary>The guids the actor selected (at most 50).</summary>
    public IReadOnlyList<ulong> Selection { get; init; } = [];

    /// <summary>The unit a selection-reading command acts on: one of <see cref="Selection"/>, or 0 for none.</summary>
    public ulong Primary { get; init; }

    /// <summary>The clicked point, or null when the action had none.</summary>
    public GmClickPoint? Point { get; init; }

    /// <summary>Attach <paramref name="action"/> to <paramref name="context"/> (replacing an earlier one). Any thread.</summary>
    public static void Attach(CommandContext context, GmActionContext action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        Attached.AddOrUpdate(context, action);
    }

    /// <summary>The action context attached to <paramref name="context"/>, or null (a player, the console, or a remote invoker no tool drives). Any thread.</summary>
    public static GmActionContext? Of(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Attached.TryGetValue(context, out GmActionContext? found) ? found : null;
    }

    /// <summary>
    /// The unit with guid <paramref name="guid"/>: an online player, else a creature on any map that has a creature system; null for none (or
    /// the empty guid). Stands in for the target resolver of the debug-command plan until it is on the base. World thread only.
    /// </summary>
    internal static Unit? FindUnit(CommandContext context, ulong guid) => FindUnit(context.World, context.Session.Services, guid);

    /// <summary><see cref="FindUnit(CommandContext, ulong)"/> without a command context (the channel checks its targets before it builds one). World thread only.</summary>
    internal static Unit? FindUnit(WorldRuntime world, IServiceProvider services, ulong guid)
    {
        var id = new ObjectGuid(guid);
        if (id.IsEmpty)
        {
            return null;
        }

        if (world.FindOnlinePlayer(id) is { } player)
        {
            return player;
        }

        if (services.GetService<CreatureWorldFeature>() is not { } feature)
        {
            return null;
        }

        foreach (CreatureMapSystem system in feature.Systems)
        {
            if (system.FindCreature(id) is { } creature)
            {
                return creature;
            }
        }

        return null;
    }
}

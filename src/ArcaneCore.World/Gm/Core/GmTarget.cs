using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Gm.Core;

/// <summary>The kinds of world object a command accepts as its target (a flag set).</summary>
[Flags]
public enum GmTargetKinds
{
    None = 0,
    Player = 1,
    Creature = 2,
    GameObject = 4,
    Transport = 8,

    /// <summary>Players and creatures (bots, pets and totems included).</summary>
    Unit = Player | Creature,

    Any = Player | Creature | GameObject | Transport,
}

/// <summary>What a command needs from its target.</summary>
/// <param name="Accepted">The object kinds it can act on (a target of another kind is refused).</param>
/// <param name="Write">
/// The command changes the target. <see cref="GmTargetResolver.ResolveThen(CommandContext, GmSelector, GmTargetRequest, Action{GmTarget})"/>
/// then hands the target over through <see cref="GmTarget.RunOnOwningMap"/> (the plan's <c>World.Post</c> rule for cross-map and console writes).
/// </param>
/// <param name="AllowOffline">The command handles <see cref="GmSelectorKind.Offline"/> (the resolver returns the name; the command reads the character record off the world thread).</param>
public readonly record struct GmTargetRequest(GmTargetKinds Accepted = GmTargetKinds.Any, bool Write = false, bool AllowOffline = false)
{
    public static GmTargetRequest Reading { get; } = new(GmTargetKinds.Any);

    public static GmTargetRequest Writing { get; } = new(GmTargetKinds.Any, Write: true);
}

/// <summary>A resolved target: a live object, or the name of an offline character.</summary>
public sealed record GmTarget(WorldObject? Object, string? OfflineName = null)
{
    public bool IsOffline => OfflineName is not null;

    public Unit? Unit => Object as Unit;

    public Player? Player => Object as Player;

    public Creature? Creature => Object as Creature;

    public GameObject? GameObject => Object as GameObject;

    /// <summary>
    /// Run a write against this target where it is safe (plan 1.3, 3.2 rule 7). Inline when the issuer is a player on the target's own map
    /// (the issuer's map thread owns it); otherwise, and always for the console and remote administration, queued with
    /// <c>World.Post</c> so it runs at the start of the next tick on the world thread, when no map updates. The queued action is skipped
    /// when the issuer can no longer receive replies or the object has left its map meanwhile (the issuer is told); the command stays open
    /// (<see cref="CommandContext.Defer"/>) until the action ran. An offline target has no map: it runs inline for a player and queued for the console.
    /// </summary>
    public void RunOnOwningMap(CommandContext context, Action<GmTarget> action)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        bool sameMap = !context.IsConsole && (Object is null || ReferenceEquals(Object.Map, context.Player.Map)) && context.World.IsWorldThread;
        if (sameMap)
        {
            action(this);
            return;
        }

        IDisposable hold = context.Defer();
        context.World.Post(() =>
        {
            try
            {
                if (!context.CanReplyLater)
                {
                    return;
                }

                if (Object is not null && !Object.IsInWorld)
                {
                    context.Fail("target-gone");
                    context.Reply(GmTargetResolver.TargetGone);
                    return;
                }

                action(this);
            }
            finally
            {
                hold.Dispose();
            }
        });
    }
}

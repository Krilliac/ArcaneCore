using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Owns the world's <see cref="GmStream"/> and runs it on the world thread: each tick it ends timed-out subscriptions and sends
/// the queued lines to the subscribed GMs as system messages; a GM that logs out loses every subscription. Game code publishes
/// through <see cref="WorldRuntime.GmTrace"/> (set here) and guards its decoding and formatting with <see cref="GmTraceGuard.Wants"/>;
/// other World code may use <c>GetService&lt;GmStreamFeature&gt;()?.Stream</c> and <see cref="GmStream.IsActive"/>. Each tick, before the
/// lines are sent, the feature refreshes every subscribed GM's position and selection (<see cref="GmStream.RefreshViewers"/>) for the
/// scope test, using <see cref="GmStreamOptions.ScopeRange"/> as the range.
/// </summary>
public sealed class GmStreamFeature : IWorldFeature
{
    private readonly Action<GmStreamLine> _deliver;
    private readonly Func<ObjectGuid, GmStreamViewer?> _resolveViewer;
    private WorldRuntime? _world;

    public GmStreamFeature()
    {
        _deliver = Deliver;
        _resolveViewer = ResolveViewer;
    }

    /// <summary>The stream (default limits).</summary>
    public GmStream Stream { get; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.GmTrace = Stream;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.Updated += OnUpdated;
    }

    private void OnPlayerLoggingOut(Player player) => Stream.UnsubscribeAll(player.Guid);

    private void OnUpdated(uint diffMs)
    {
        if (Stream.IsActive)
        {
            Stream.Sweep();
            Stream.RefreshViewers(_resolveViewer);
        }

        Stream.Drain(_deliver);
    }

    private GmStreamViewer? ResolveViewer(ObjectGuid gm)
    {
        if (_world?.FindOnlinePlayer(gm) is not { Map: { } map } player)
        {
            return null;
        }

        return new GmStreamViewer(player.Selection, map.MapId, map.InstanceId, player.X, player.Y, player.Z, Stream.Options.ScopeRange);
    }

    private void Deliver(GmStreamLine line) => _world?.FindOnlinePlayer(line.Gm)?.SendSystemMessage(line.Text);
}

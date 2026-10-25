using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Owns the world's <see cref="GmStream"/> and runs it on the world thread: each tick it ends timed-out subscriptions and sends
/// the queued lines to the subscribed GMs as system messages; a GM that logs out loses every subscription. Producers reach the
/// stream with <c>GetService&lt;GmStreamFeature&gt;()?.Stream</c> and guard their formatting with <see cref="GmStream.IsActive"/>.
/// </summary>
public sealed class GmStreamFeature : IWorldFeature
{
    private readonly Action<GmStreamLine> _deliver;
    private WorldRuntime? _world;

    public GmStreamFeature() => _deliver = Deliver;

    /// <summary>The stream (default limits).</summary>
    public GmStream Stream { get; } = new();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.Updated += OnUpdated;
    }

    private void OnPlayerLoggingOut(Player player) => Stream.UnsubscribeAll(player.Guid);

    private void OnUpdated(uint diffMs)
    {
        if (Stream.IsActive)
        {
            Stream.Sweep();
        }

        Stream.Drain(_deliver);
    }

    private void Deliver(GmStreamLine line) => _world?.FindOnlinePlayer(line.Gm)?.SendSystemMessage(line.Text);
}

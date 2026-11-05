using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// Owns the world's <see cref="GmStream"/> and runs it on the world thread: each tick it ends timed-out subscriptions and sends
/// the queued lines to the subscribed GMs as system messages; a GM that logs out loses every subscription, and so does one that leaves the world through a committed cluster
/// transfer (<see cref="WorldRuntime.RetireDetachedPlayer"/> raises <see cref="WorldRuntime.PlayerLoggingOut"/>). A session caches its
/// rank at login and nothing tells a live session when the account is demoted (the realm console's <c>account set-gmlevel</c> only applies at the
/// next login), so the feature re-reads each subscribed GM's account from <see cref="IAccountStore"/> every <see cref="RankRecheckMs"/> ms,
/// off the world thread: a subscriber whose stored rank is below <see cref="AccountSecurity.GameMaster"/> (or whose account is gone) loses every
/// subscription on the next tick. A demotion therefore ends the feed within about a second plus one store read, not at the next login. If the
/// store read fails the cached rank stands and the read is retried at the next interval. Game code publishes
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

    /// <summary>How often (world-clock ms) each subscribed GM's account rank is re-read from the account store.</summary>
    public const uint RankRecheckMs = 1000;

    private readonly ConcurrentDictionary<ObjectGuid, byte> _inFlight = new();
    private readonly ConcurrentDictionary<ObjectGuid, (WorldSession Session, AccountSecurity Rank)> _storedRank = new();
    private uint _sinceRecheckMs;

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

    private void OnPlayerLoggingOut(Player player)
    {
        Stream.UnsubscribeAll(player.Guid);
        _storedRank.TryRemove(player.Guid, out _);
    }

    private void OnUpdated(uint diffMs)
    {
        if (Stream.IsActive)
        {
            Stream.Sweep();
            EnforceStoredRanks(diffMs);
            Stream.RefreshViewers(_resolveViewer);
        }
        else if (!_storedRank.IsEmpty)
        {
            _storedRank.Clear();
        }

        Stream.Drain(_deliver);
    }

    /// <summary>World thread. Ends the subscriptions of every GM whose stored rank is below GameMaster and starts the next store reads.</summary>
    private void EnforceStoredRanks(uint diffMs)
    {
        IReadOnlyList<ObjectGuid> subscribers = Stream.Subscribers();
        foreach (ObjectGuid gm in subscribers)
        {
            if (IsDemotedInStore(gm))
            {
                Stream.UnsubscribeAll(gm);
                _storedRank.TryRemove(gm, out _);
            }
        }

        _sinceRecheckMs = _sinceRecheckMs > uint.MaxValue - diffMs ? uint.MaxValue : _sinceRecheckMs + diffMs;
        if (_sinceRecheckMs < RankRecheckMs)
        {
            return;
        }

        _sinceRecheckMs = 0;
        foreach (ObjectGuid gm in subscribers)
        {
            if (_world?.FindOnlinePlayer(gm) is { Session: WorldSession { AccountName.Length: > 0 } session }
                && session.Services.GetService<IAccountStore>() is { } store
                && _inFlight.TryAdd(gm, 0))
            {
                _ = RecheckAsync(gm, session, store);
            }
        }
    }

    private bool IsDemotedInStore(ObjectGuid gm)
        => _storedRank.TryGetValue(gm, out var stored)
            && stored.Rank < AccountSecurity.GameMaster
            && _world?.FindOnlinePlayer(gm) is { } player
            && ReferenceEquals(player.Session, stored.Session);

    /// <summary>Thread pool. Reads the account's current rank and records it for the world thread; never throws.</summary>
    private async Task RecheckAsync(ObjectGuid gm, WorldSession session, IAccountStore store)
    {
        try
        {
            Account? account = await store.FindByUsernameAsync(session.AccountName).ConfigureAwait(false);
            _storedRank[gm] = (session, account?.Security ?? AccountSecurity.Player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            session.Services.GetService<ILogger<GmStreamFeature>>()?.LogFault("gm.stream-rank-recheck", ex,
                "GM stream: re-reading the rank of account {Account} failed; the cached rank stands until the next interval", session.AccountId);
        }
        finally
        {
            _inFlight.TryRemove(gm, out _);
        }
    }

    private GmStreamViewer? ResolveViewer(ObjectGuid gm)
    {
        if (_world?.FindOnlinePlayer(gm) is not { Map: { } map } player)
        {
            return null;
        }

        return new GmStreamViewer(player.Selection, map.MapId, map.InstanceId, player.X, player.Y, player.Z, Stream.Options.ScopeRange);
    }

    private void Deliver(GmStreamLine line)
    {
        if (_world?.FindOnlinePlayer(line.Gm) is not { } player)
        {
            return;
        }

        if (player.Security < AccountSecurity.GameMaster || IsDemotedInStore(line.Gm))
        {
            Stream.UnsubscribeAll(line.Gm); // Drain delivers outside the stream's lock, so this is safe here
            _storedRank.TryRemove(line.Gm, out _);
            return;
        }

        player.SendSystemMessage(line.Text);
    }
}

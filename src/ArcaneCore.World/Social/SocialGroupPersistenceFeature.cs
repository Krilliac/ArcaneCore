using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Features;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>
/// Parties and raids survive a world restart (vmangos keeps them in <c>groups</c> / <c>group_member</c>, saved by
/// Group::Create, AddMember, RemoveMember, ChangeLeader, SetAssistant, SetLootMethod, ConvertToRaid and the subgroup moves,
/// and read back by ObjectMgr::LoadGroups at start). At attach the stored groups are restored into the social feature's
/// <see cref="GroupManager"/> (<see cref="GroupManager.Restore"/>), before the world thread runs; rows of groups that cannot
/// be restored are deleted. While the world runs, once per clock second on the world thread, every created group is
/// compared with what was last written and only a changed group is written again (one whole-group snapshot per key through
/// <see cref="KeyedStoreWriteQueue{TStore}"/>, newest wins); a group that is gone is deleted. vmangos writes at each
/// mutation; here a change reaches storage within a second, and the stop writes the final state before the queue drains.
/// Without an <see cref="IGroupStore"/> (no database) groups live in memory only, as before. A store that cannot be read at
/// start fails the start (fail closed), so a restart never forgets the groups and then overwrites their rows.
/// <para>
/// The name sorts after <see cref="SocialFeature"/>, whose context must exist when this attaches (features attach in
/// full-name order).
/// </para>
/// </summary>
public sealed class SocialGroupPersistenceFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers)
    : IWorldFeature
{
    private readonly ILogger _logger = loggers.CreateLogger<SocialGroupPersistenceFeature>();
    private readonly Dictionary<uint, GroupRecord> _written = [];
    private GroupManager? _groups;
    private GroupAuthority? _authority;
    private ArcaneCore.Game.Social.ICharacterLookup? _characters;
    private bool _holding;
    private bool _reloading;
    private bool _stopped;
    private int _holdGeneration;
    private WorldRuntime? _world;
    private TimeProvider _clock = TimeProvider.System;
    private long _lastSweep = long.MinValue;

    /// <summary>The write queue (exposed for health reporting and tests); null without a store.</summary>
    public KeyedStoreWriteQueue<IGroupStore>? Writes { get; private set; }

    /// <summary>What the start restored and dropped; null before attach or without a store.</summary>
    public GroupRestoreResult? Restored { get; private set; }

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (services.GetService<SocialFeature>() is not { } social)
        {
            return;
        }

        IReadOnlyList<GroupRecord> stored;
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IGroupStore>() is not { } store)
            {
                return; // no database: groups stay in memory only
            }

            stored = store.LoadGroupsAsync().GetAwaiter().GetResult();
        }

        _world = world;
        _clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _groups = social.Context.Groups;
        _characters = social.Context.Characters;

        // Cluster mode (S3a): the Social lease holder's group authority owns the groups, so that is what is restored and written, and only while this node holds the lease.
        _authority = _groups.Authority is not null ? services.GetService<PresenceHolderService>()?.Groups : null;
        Writes = new KeyedStoreWriteQueue<IGroupStore>(scopes, loggers.CreateLogger<KeyedStoreWriteQueue<IGroupStore>>(), "group", services.GetService<ShutdownDrain>()?.Features);
        Writes.Start();

        if (_authority is not null)
        {
            _holding = HoldsSocial();
            if (_holding)
            {
                Restored = _authority.Restore(stored, _characters);
            }
            else
            {
                Restored = new GroupRestoreResult([], []);
                stored = [];
            }
        }
        else
        {
            Restored = _groups.Restore(stored);
        }

        Dictionary<uint, GroupRecord> byId = stored.ToDictionary(r => r.Id);
        foreach (uint id in Restored.Restored)
        {
            _written[id] = byId[id];
        }

        foreach (uint id in Restored.Dropped)
        {
            Writes.Save(Key(id), store => store.DeleteGroupAsync(id));
        }

        if (stored.Count > 0)
        {
            _logger.LogInformation("Restored {Restored} groups ({Dropped} dropped)", Restored.Restored.Count, Restored.Dropped.Count);
        }

        world.Updated += OnUpdated;
    }

    /// <summary>Write the final state, then drain the queue.</summary>
    public async Task StopAsync()
    {
        if (Writes is null)
        {
            return;
        }

        _stopped = true; // no reload starts from here on: the final Sync below only writes
        if (_world is not null)
        {
            _world.Updated -= OnUpdated;
        }

        // The world thread no longer runs (IWorldFeature.StopAsync), so the group table is quiet.
        Sync();
        await Writes.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Compare and write now: the world tick does it once per second, and a handoff's freeze (<see cref="Cluster.Handoff.LocalWorldHandoffSource"/>) does it on the simulation thread as it parks, so a group change
    /// made in the last second before the freeze is queued, and then drained by the flush, instead of being in no queue at all. World thread only (tests use it through the world thread too).
    /// </summary>
    public void SyncNow() => Sync();

    private void OnUpdated(uint diffMs)
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (now == _lastSweep)
        {
            return;
        }

        _lastSweep = now;
        Sync();
    }

    private void Sync()
    {
        if (_groups is null || Writes is null)
        {
            return;
        }

        IReadOnlyList<GroupRecord> current;
        if (_authority is { } authority)
        {
            if (!HoldsSocial())
            {
                _holding = false; // another node owns the table now; it writes
                _holdGeneration++; // a reload still in flight belongs to the lease that is gone
                _reloading = false;
                return;
            }

            if (!_holding)
            {
                // The groups-table read runs off the tick; _holding stays false (nothing is written) until ApplyReload runs on a later world tick. Limit: a group command that reaches the local authority between the gain and the apply (LeaseRoutedPresence routes locally as soon as the lease is held) is wiped by the apply's Reset; the window is the read time plus one tick, the old synchronous reload had up to one sweep.
                BeginReloadAuthority(authority);
                return;
            }

            // The offline-leader handover of the whole realm runs here, at the holder, about once a second (the workers' mirrors never do it).
            authority.UpdateOfflineLeaders();
            current = authority.Snapshots();
        }
        else
        {
            current = [.. _groups.Groups.Select(GroupManager.Snapshot)];
        }

        var seen = new HashSet<uint>();
        foreach (GroupRecord snapshot in current)
        {
            seen.Add(snapshot.Id);
            if (_written.TryGetValue(snapshot.Id, out GroupRecord? before) && snapshot.SameAs(before))
            {
                continue;
            }

            _written[snapshot.Id] = snapshot;
            GroupRecord toWrite = snapshot;
            Writes.Save(Key(snapshot.Id), store => store.SaveGroupAsync(toWrite));
        }

        foreach (uint gone in _written.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _written.Remove(gone);
            Writes.Save(Key(gone), store => store.DeleteGroupAsync(gone));
        }
    }

    /// <summary>Whether this node holds the Social lease (always true without a lease keeper).</summary>
    private bool HoldsSocial()
        => services.GetService<RealmAuthorityLeaseKeeper>() is not { } keeper || keeper.TokenFor(RealmAuthorityScopes.Social) is not null;

    /// <summary>
    /// The lease moved to this node: the authority is rebuilt from the stored groups (the previous holder wrote its changes within a second of making them), so a failover
    /// continues from storage. World thread: the groups-table read runs on the thread pool (the SQLite provider runs its async calls synchronously, so it must not run on the
    /// tick) and <see cref="ApplyReload"/> applies it on a later world tick; <c>_holding</c> stays false until then. Once per gain; a read that fails is retried on the next sweep.
    /// </summary>
    private void BeginReloadAuthority(GroupAuthority authority)
    {
        WorldRuntime? world = _world;
        if (_reloading || _stopped || world is null)
        {
            return;
        }

        _reloading = true;
        int generation = _holdGeneration;
        _ = Task.Run(async () =>
        {
            IReadOnlyList<GroupRecord>? stored = null;
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                if (scope.ServiceProvider.GetService<IGroupStore>() is { } store)
                {
                    stored = await store.LoadGroupsAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("social.group-reload-read", ex, "Social lease gained but the stored groups could not be read; retrying");
            }

            try
            {
                world.Post(() => ApplyReload(authority, stored, generation));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // world.Post can throw once the world has shut down; the reload is dropped, but not silently.
                _logger.LogFault("social.group-reload-post", ex, "Social group reload could not be applied");
            }
        });
    }

    /// <summary>World thread: apply the groups read for a lease gain. Dropped when the lease was lost since (<paramref name="generation"/> moved); a null read (failed or no store) leaves <c>_holding</c> false so the next sweep reads again.</summary>
    private void ApplyReload(GroupAuthority authority, IReadOnlyList<GroupRecord>? stored, int generation)
    {
        if (generation != _holdGeneration || _stopped)
        {
            return;
        }

        _reloading = false;
        if (stored is null || !HoldsSocial())
        {
            return;
        }

        authority.Reset();
        GroupRestoreResult result = authority.Restore(stored, _characters!);
        Restored = result;
        _written.Clear();
        Dictionary<uint, GroupRecord> byId = stored.ToDictionary(r => r.Id);
        foreach (uint id in result.Restored)
        {
            _written[id] = byId[id];
        }

        foreach (uint id in result.Dropped)
        {
            Writes?.Save(Key(id), store => store.DeleteGroupAsync(id));
        }

        _holding = true;
        _logger.LogInformation("Social lease gained: {Restored} groups loaded from storage ({Dropped} dropped)", result.Restored.Count, result.Dropped.Count);
    }

    private static string Key(uint groupId) => "group:" + groupId;
}

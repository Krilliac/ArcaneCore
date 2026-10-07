using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Features;
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
        Writes = new KeyedStoreWriteQueue<IGroupStore>(scopes, loggers.CreateLogger<KeyedStoreWriteQueue<IGroupStore>>(), "group");
        Writes.Start();

        Restored = _groups.Restore(stored);
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

        if (_world is not null)
        {
            _world.Updated -= OnUpdated;
        }

        // The world thread no longer runs (IWorldFeature.StopAsync), so the group table is quiet.
        Sync();
        await Writes.StopAsync().ConfigureAwait(false);
    }

    /// <summary>Compare and write now (tests; the world tick does it once per second).</summary>
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

        var seen = new HashSet<uint>();
        foreach (Group group in _groups.Groups)
        {
            seen.Add(group.Id);
            GroupRecord snapshot = GroupManager.Snapshot(group);
            if (_written.TryGetValue(group.Id, out GroupRecord? before) && snapshot.SameAs(before))
            {
                continue;
            }

            _written[group.Id] = snapshot;
            Writes.Save(Key(group.Id), store => store.SaveGroupAsync(snapshot));
        }

        foreach (uint gone in _written.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _written.Remove(gone);
            Writes.Save(Key(gone), store => store.DeleteGroupAsync(gone));
        }
    }

    private static string Key(uint groupId) => "group:" + groupId;
}

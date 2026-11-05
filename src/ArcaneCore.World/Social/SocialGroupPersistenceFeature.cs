using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Characters;
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
    /// <summary>How long one reread after a lease gain may take before it counts as failed and is tried again.</summary>
    private static readonly TimeSpan ReloadBudget = TimeSpan.FromSeconds(30);

    private const int ReloadRetryDelayMs = 1000;

    private readonly ILogger _logger = loggers.CreateLogger<SocialGroupPersistenceFeature>();
    private readonly Dictionary<uint, GroupRecord> _written = [];
    private readonly CancellationTokenSource _stop = new();
    private GroupManager? _groups;
    private GroupAuthority? _authority;
    private ArcaneCore.Game.Social.ICharacterLookup? _characters;
    private CharacterDirectory? _directory;
    private bool _holding;
    private Task? _reload;
    private bool _reloading; // world thread
    private long _reloadNotBeforeMs; // world thread: a failed read is tried again from then on
    private volatile bool _stopping;
    private WorldRuntime? _world;
    private TimeProvider _clock = TimeProvider.System;
    private long _lastSweep = long.MinValue;

    /// <summary>The write queue (exposed for health reporting and tests); null without a store.</summary>
    public KeyedStoreWriteQueue<IGroupStore>? Writes { get; private set; }

    /// <summary>World thread: whether the authority table here is the one read from storage for this holding of the Social lease.</summary>
    internal bool AuthorityLoaded => _holding;

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
        _directory = social.Characters;

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

        if (_world is not null)
        {
            _world.Updated -= OnUpdated;
        }

        // A reread still running stores nothing: the rows stay as they are, and the table it was going to replace is not written.
        _stopping = true;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_reload is { } reload)
        {
            await reload.ConfigureAwait(false);
        }

        _authority?.DropHeldCommands();

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

        IReadOnlyList<GroupRecord> current;
        if (_authority is { } authority)
        {
            if (!HoldsSocial())
            {
                _holding = false; // another node owns the table now; it writes
                return;
            }

            if (!_holding)
            {
                ReloadAuthority(authority);
                return; // nothing is written from this table until the read has replaced it
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

    /// <summary>The Social lease's token here, or null: a read started under one token is not installed under another.</summary>
    private long? SocialToken() => services.GetService<RealmAuthorityLeaseKeeper>()?.TokenFor(RealmAuthorityScopes.Social);

    /// <summary>
    /// World thread. The lease moved to this node: the authority is rebuilt from the stored groups (the previous holder wrote its changes within a second of making them), so a failover continues from storage. The read runs off
    /// the world thread, nothing is written until it is installed, and commands that reach the table meanwhile wait in it. A failed read is tried again a second later.
    /// </summary>
    private void ReloadAuthority(GroupAuthority authority)
    {
        if (_reloading || _stopping || Environment.TickCount64 < _reloadNotBeforeMs)
        {
            return;
        }

        _reloading = true;
        authority.HoldForReload();
        long? token = SocialToken();
        WorldRuntime world = _world!;
        _reload = Task.Run(() => ReadAuthorityStateAsync(world, authority, token));
    }

    private async Task ReadAuthorityStateAsync(WorldRuntime world, GroupAuthority authority, long? token)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            budget.CancelAfter(ReloadBudget);
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            IReadOnlyList<GroupRecord> stored = await scope.ServiceProvider.GetRequiredService<IGroupStore>().LoadGroupsAsync(budget.Token).ConfigureAwait(false);

            // Restore drops a group or member the directory does not know, and the drop deletes rows; this worker's directory has none of what other workers created since it started. So it is brought up to date first, and
            // after the rows, which can only name characters stored before them. If it cannot be read nothing is installed: the catch below keeps the writes held.
            await _directory!.RefreshAsync(scope.ServiceProvider.GetRequiredService<ICharacterStore>(), budget.Token).ConfigureAwait(false);
            world.Post(() => InstallAuthorityState(authority, token, stored));
        }
        catch (OperationCanceledException) when (_stopping)
        {
            // Stopping: nothing was written from the old table, and nothing is now.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault(LogLevel.Warning, "social.group-lease-gain-reload", ex, "Reading the groups after gaining the social lease failed; their writes stay held and the read is tried again");
            world.Post(RetryReloadLater);
        }
    }

    private void RetryReloadLater()
    {
        _reloading = false;
        _reloadNotBeforeMs = Environment.TickCount64 + ReloadRetryDelayMs;
    }

    /// <summary>World thread: what was read replaces the table, the held commands run on it, and the writes resume. If the lease moved during the read the rows are not this holding's, and the next gain reads again.</summary>
    private void InstallAuthorityState(GroupAuthority authority, long? token, IReadOnlyList<GroupRecord> stored)
    {
        _reloading = false;
        if (_stopping)
        {
            return;
        }

        if (SocialToken() != token)
        {
            authority.DropHeldCommands();
            return;
        }

        GroupRestoreResult result;
        try
        {
            authority.Reset();
            result = authority.Restore(stored, _characters!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RetryReloadLater();
            _logger.LogFault("social.group-lease-gain-install", ex, "Installing the groups read after gaining the social lease failed; their writes stay held and the read is tried again");
            return;
        }

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
        int failed = authority.ReleaseHeldCommands();
        _logger.LogInformation("Social lease gained: {Restored} groups loaded from storage ({Dropped} dropped, {Failed} held commands failed)", result.Restored.Count, result.Dropped.Count, failed);
    }

    private static string Key(uint groupId) => "group:" + groupId;
}

using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Instances.Scripts.Naxxramas;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Instances;

/// <summary>
/// Dungeon and raid instances in the world daemon (docs/integration/instances.md): creates the
/// <see cref="InstanceManager"/> (the world's map resolver), loads the stored instances and
/// binds from <see cref="IInstanceStore"/>, wires it to the group manager (binds follow group
/// changes), to the teleport service (homebind) and to system chat, runs the reset schedule
/// on a timer and persists changes through an ordered write queue drained at shutdown.
/// <para>Character deletion (<see cref="ICharacterDeleteHook"/>): queued instance writes drain before
/// the rows go (<c>InstanceDataModule</c> removes them), then the character's in-memory binds and
/// last instance are dropped and a purge of its rows is queued after any later write.</para>
/// <para>Options come from the <c>World:Instances</c> configuration section (<see cref="InstanceOptions"/>).</para>
/// </summary>
public sealed class InstanceFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers) : IWorldFeature, ICharacterDeleteHook, IAreaTriggerListener, IAreaTriggerGate, IAsyncDisposable
{
    /// <summary>How often the reset schedule runs (vmangos checks it every world update; resets are minute-granular).</summary>
    public const int ScheduleIntervalMs = 5000;

    /// <summary>A spatially verified client area trigger goes on to the map's instance script (ScriptDev2 AreaTrigger_at_* scripts).</summary>
    public void OnAreaTrigger(Player player, uint triggerId)
        => player.Map?.FindUpdater<Game.Instances.Scripts.InstanceData>()?.OnAreaTrigger(player, triggerId);

    /// <summary>mangos-classic naxxramas.cpp DoHandleAreaTrigger(AREATRIGGER_FROSTWYRM_TELE): all four wing bosses gate trigger 4156.</summary>
    public AreaTriggerVerdict Check(Player player, AreaTriggerTeleport teleport)
        => teleport.Id == 4156 && player.MapId == 533
            && player.Map?.FindUpdater<Game.Instances.Scripts.InstanceData>() is not NaxxramasInstance { FrostwyrmUnlocked: true }
                ? AreaTriggerVerdict.Refuse(null) : AreaTriggerVerdict.Allow;

    /// <summary>Upper bound for draining the write queue or one world-thread round trip during a character deletion.</summary>
    public static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggers.CreateLogger<InstanceFeature>();
    private InstanceManager? _manager;
    private InstanceWriteQueue? _writes;
    private Timer? _scheduleTimer;
    private WorldRuntime? _world;
    private Task? _stopped;
    private readonly Lock _lifecycleLock = new();

    /// <summary>The instance manager (world thread only; available after <see cref="Attach"/>).</summary>
    public InstanceManager Instances => _manager ?? throw new InvalidOperationException("the instance feature is not attached");

    /// <summary>
    /// Raised on the world thread for every instance that is deleted, including the saves dropped at load (see <see cref="InstanceWriteQueue.InstanceRemoved"/>).
    /// Subscribe from a world command, after <see cref="Attach"/> ran and before the load command does.
    /// </summary>
    public event Action<uint>? InstanceRemoved
    {
        add => (_writes ?? throw new InvalidOperationException("the instance feature is not attached")).InstanceRemoved += value;
        remove
        {
            if (_writes is { } writes)
            {
                writes.InstanceRemoved -= value;
            }
        }
    }

    public InstanceOptions Options { get; } = new();

    /// <summary>How long the post-delete drain waits for the queued removal (tests shorten it).</summary>
    public TimeSpan DrainTimeout { get; init; } = DeleteTimeout;

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    /// <summary>Wait until every queued write has been attempted (tests).</summary>
    public Task FlushAsync() => _writes?.FlushAsync() ?? Task.CompletedTask;

    /// <summary>The number of instance writes queued so far (world thread: read it when an operation starts).</summary>
    public long WriteWatermark => _writes?.Enqueued ?? 0;

    /// <summary>Wait until the writes counted by <paramref name="watermark"/> were attempted (for example a queued <c>InstanceSaved</c>).</summary>
    public Task WaitForWritesAsync(long watermark, CancellationToken cancellationToken)
        => _writes?.WaitForAsync(watermark, cancellationToken) ?? Task.CompletedTask;

    public void Attach(WorldRuntime world)
    {
        _world = world;
        services.GetService<IConfiguration>()?.GetSection(InstanceOptions.SectionName).Bind(Options);
        InstanceStoreSnapshot snapshot = InstanceStoreSnapshot.Empty;
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IInstanceStore>() is { } store)
            {
                snapshot = store.LoadAsync().GetAwaiter().GetResult();
            }
        }

        _writes = new InstanceWriteQueue(scopes, loggers.CreateLogger<InstanceWriteQueue>());
        _writes.Start();
        // vmangos DungeonResetScheduler::ScheduleAllDungeonResets/Update
        // (Maps/MapPersistentStateMgr.cpp:448-528,570-619) use Unix time for global raid resets.
        // The host's TimeProvider supplies that clock and lets acceptance tests advance it.
        TimeProvider time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _manager = new InstanceManager(world, Options, _writes,
            unixNow: () => time.GetUtcNow().ToUnixTimeSeconds(), logger: loggers.CreateLogger<InstanceManager>());
        QuestNpcFeature? questFeature = services.GetService<QuestNpcFeature>();
        // cmangos Player::IsCurrentQuest mode 2: QUEST_STATUS_COMPLETE and not rewarded (the SD2 Fortune Awaits chest check).
        _manager.QuestCompleteUnrewarded = (player, questId) => questFeature?.Services.IsCurrent(player, questId, 2) == true;
        _manager.ScriptCreatureCredit = (player, entry, guid) => services.GetService<QuestNpcFeature>()?.Services.KilledMonsterCredit(player, entry, guid);
        _manager.ScriptCastPlayerSpell = (player, spell) => services.GetService<SpellFeature>()?.System.CastSpell(player, spell,
            SpellCastTargets.ForSelf(), triggered: true);
        _manager.SystemMessage = static (player, text) => player.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(text));
        _manager.Install();
        world.PlayerLoggedIn += OnPlayerLoggedIn;

        // Features attach in type-name order: the map registry (TeleportFeature) and the groups
        // (SocialFeature) are ready once the world thread runs its first commands.
        InstanceManager manager = _manager;
        world.Post(() =>
        {
            if (services.GetService<SocialFeature>() is { } social)
            {
                Game.Groups.GroupManager groups = social.Context.Groups;
                manager.GroupOf = groups.GetGroup;
                groups.MemberAdded += manager.OnGroupMemberAdded;
                groups.MemberRemoved += manager.OnGroupMemberRemoved;
                groups.Disbanding += manager.OnGroupDisbanding;
                groups.LeaderChanged += manager.OnGroupLeaderChanged;
            }

            if (services.GetService<TeleportFeature>() is { } teleport)
            {
                manager.TeleportToHomebind = teleport.Teleports.TeleportToHomebind;
            }

            manager.Load(snapshot);

            // Groups restored from storage (SocialGroupPersistenceFeature restores them in its Attach, before the world thread
            // runs) take back their leader's stored permanent binds, as vmangos ObjectMgr::LoadGroups attaches the group_instance
            // rows of each group it loads (ObjectMgr.cpp:5463-5513). A group formed later gets them in OnGroupMemberAdded.
            if (services.GetService<SocialFeature>() is { } restored)
            {
                foreach (Game.Groups.Group group in restored.Context.Groups.Groups)
                {
                    manager.RestoreStoredGroupBinds(group);
                }
            }
        });
        _scheduleTimer = new Timer(_ => world.Post(manager.UpdateSchedule), null, ScheduleIntervalMs, ScheduleIntervalMs);
        _logger.LogInformation("instances: {Instances} stored, {Binds} character binds", snapshot.Instances.Count, snapshot.Binds.Count);
    }

    /// <summary>Queued binds of the character must not land after its rows are removed.</summary>
    public Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character)
        => _writes is { } writes ? writes.FlushAsync().WaitAsync(DeleteTimeout) : Task.CompletedTask;

    /// <summary>Drop the deleted character's binds and last instance (vmangos Player::DeleteFromDB: character_instance).</summary>
    public async Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (_world is not { } world || _manager is not { } manager)
        {
            return;
        }

        var guid = ObjectGuid.Player((uint)character.Id);
        await world.InvokeAsync(() =>
        {
            manager.DeleteCharacter(guid);

            // After the unbind writes just queued and anything earlier that still names the character.
            _writes?.CharacterDeleted(character.Id);
            return true;
        }).WaitAsync(DeleteTimeout).ConfigureAwait(false);

        // The deletion completes only after the queued removal was attempted (it is conditional on
        // the id still having no character row, so a recreated character keeps its binds).
        if (_writes is { } writes)
        {
            await writes.FlushAsync().WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            return _stopped ??= StopCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        if (_world is not null)
        {
            _world.PlayerLoggedIn -= OnPlayerLoggedIn;
        }

        if (_scheduleTimer is not null)
        {
            await _scheduleTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_writes is not null)
        {
            await _writes.StopAsync().ConfigureAwait(false);
        }
    }

    // The client asks for SMSG_RAID_INSTANCE_INFO itself (CMSG_REQUEST_RAID_INFO); a player with
    // raid locks also gets it right after login so the lockout list is current at once.
    private void OnPlayerLoggedIn(Player player)
    {
        if (_manager is { } manager && manager.GetPlayerBinds(player.Guid).Any(b => b.Permanent))
        {
            manager.SendRaidInfo(player);
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
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
/// <para>Options come from the <c>World:Instances</c> configuration section (<see cref="InstanceOptions"/>).</para>
/// </summary>
public sealed class InstanceFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers) : IWorldFeature, IAsyncDisposable
{
    /// <summary>How often the reset schedule runs (vmangos checks it every world update; resets are minute-granular).</summary>
    public const int ScheduleIntervalMs = 5000;

    private readonly ILogger _logger = loggers.CreateLogger<InstanceFeature>();
    private InstanceManager? _manager;
    private InstanceWriteQueue? _writes;
    private Timer? _scheduleTimer;
    private WorldRuntime? _world;
    private Task? _stopped;
    private readonly Lock _lifecycleLock = new();

    /// <summary>The instance manager (world thread only; available after <see cref="Attach"/>).</summary>
    public InstanceManager Instances => _manager ?? throw new InvalidOperationException("the instance feature is not attached");

    public InstanceOptions Options { get; } = new();

    /// <summary>Writes queued or in progress.</summary>
    public int PendingWrites => _writes?.Pending ?? 0;

    /// <summary>Wait until every queued write has been attempted (tests).</summary>
    public Task FlushAsync() => _writes?.FlushAsync() ?? Task.CompletedTask;

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
        _manager = new InstanceManager(world, Options, _writes, logger: loggers.CreateLogger<InstanceManager>());
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
        });
        _scheduleTimer = new Timer(_ => world.Post(manager.UpdateSchedule), null, ScheduleIntervalMs, ScheduleIntervalMs);
        _logger.LogInformation("instances: {Instances} stored, {Binds} character binds", snapshot.Instances.Count, snapshot.Binds.Count);
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

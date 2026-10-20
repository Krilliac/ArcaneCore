using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>
/// The real <see cref="IEventScenarioHost"/>: the managed playerbot service for the registry (create, start, snapshot), the services the
/// GM commands wrap for the rest (<see cref="CharacterBoost"/> through the <see cref="Population.PopulationProvisioner"/>'s <see cref="CommandContext"/>
/// pattern, <see cref="TeleportService"/>, <c>SetDefend</c>, the group coordinator's siege), called directly and with no GM session.
/// <para>
/// Thread affinity: every member runs on the world thread. <see cref="BeginCreate"/> and <see cref="BeginStart"/> start a task on the
/// managed service (which serialises its registry operations itself) and hand the result back to the world thread; the tasks are tracked
/// so a detach can wait for them (<see cref="IdleAsync"/>). The host holds no bot object between calls.
/// </para>
/// </summary>
internal sealed class ManagedEventScenarioHost(IServiceProvider services, WorldRuntime world, ManagedPlayerbotFeature bots, ILogger logger) : IEventScenarioHost
{
    private readonly object _gate = new();
    private readonly List<Task> _inFlight = [];
    private bool _closed;

    public uint NowMs => world.NowMs;

    public IReadOnlyList<PlayerbotStatus> Roster() => bots.Snapshot();

    public ScenarioBotProbe? Probe(Guid botId)
    {
        if (bots.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player) return null;
        string? held = bots.HeldFromBrain(botId);
        if (held is null && InGroup(player)) held = "in a group";
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        return new ScenarioBotProbe(player.IsAlive && !ghost, player.Combat.IsInCombat, player.Level, player.Team == Team.Horde ? (byte)1 : (byte)0,
            player.MapId, new Vector3(player.X, player.Y, player.Z), held, PlayerbotDefend.PostOf(player)?.PostId);
    }

    private bool InGroup(Player player)
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups.GetGroup(player.Guid) is not null;
        }
        catch (InvalidOperationException)
        {
            return false; // the social feature is not attached: no group can hold the bot
        }
    }

    public int CreateRoom()
    {
        PlayerbotOptions options = services.GetService<IOptions<PlayerbotOptions>>()?.Value ?? new();
        IReadOnlyList<PlayerbotStatus> all = bots.Snapshot();
        int running = 0;
        foreach (PlayerbotStatus status in all)
            if (status.State is ManagedPlayerbotState.Running or ManagedPlayerbotState.Starting) running++;
        return Math.Min(options.MaxRegisteredBots - all.Count, options.MaxBots - running);
    }

    public void BeginCreate(string name, byte race, byte characterClass, Action<PlayerbotOperationResult> done)
    {
        if (Closed) done(new PlayerbotOperationResult(false, "detaching"));
        else Track(bots.CreateAsync(name, race, characterClass), done);
    }

    public void BeginStart(Guid botId, Action<PlayerbotOperationResult> done)
    {
        if (Closed) done(new PlayerbotOperationResult(false, "detaching"));
        else Track(bots.StartAsync(botId.ToString()), done);
    }

    private void Track(Task<PlayerbotOperationResult> operation, Action<PlayerbotOperationResult> done)
    {
        Task finished = Finish(operation, done);
        lock (_gate)
        {
            _inFlight.RemoveAll(static t => t.IsCompleted);
            _inFlight.Add(finished);
        }
    }

    private async Task Finish(Task<PlayerbotOperationResult> operation, Action<PlayerbotOperationResult> done)
    {
        PlayerbotOperationResult result;
        try
        {
            result = await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new PlayerbotOperationResult(false, "failed: " + ex.GetType().Name);
        }

        try
        {
            await world.InvokeAsync(() =>
            {
                done(result);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The world is stopping, or the engine was dropped by a detach: nothing is waiting for the answer.
            logger.LogDebug(ex, "Bot event scenario: the answer to a bot operation could not be delivered ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>Stop taking results and wait for the operations in flight (a detach); off the world thread.</summary>
    public async Task IdleAsync(CancellationToken cancellationToken)
    {
        Task[] pending;
        lock (_gate)
        {
            _closed = true;
            pending = [.. _inFlight];
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether <see cref="IdleAsync"/> ran.</summary>
    public bool Closed
    {
        get
        {
            lock (_gate) return _closed;
        }
    }

    public string? Boost(Guid botId, int level)
    {
        if (bots.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player) return "bot-not-running";
        if (level <= player.Level) return null;
        // The checks the population provisioner makes before CharacterBoost changes the character.
        if (!player.IsAlive) return "bot-dead";
        if (player.Combat.IsInCombat) return "bot-in-combat";
        if (!player.CanMutateQuestSettlementState) return "bot-settling-quest";
        BoostEnvironment? environment = BoostEnvironment.Resolve(services, out string missing);
        if (environment is null) return "boost-unavailable: " + missing;
        var context = new CommandContext(session, player, services.GetRequiredService<CommandTable>());
        CharacterBoost.Run(context, player, checked((byte)level), environment);
        return null;
    }

    public IReadOnlyList<FleetSlot> Ring(uint map, Vector3 anchor, float orientation, int count)
    {
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        TerrainInfo? info = terrain.Enabled ? terrain.For(map) : null;
        // The ground under a slot, the way .playerbot send <place> asks for it, but only where it agrees with the anchor's height:
        // a map such as the Undercity has its floors below the terrain surface, and a slot on the surface above is no slot.
        return PlayerbotFleetPlacement.Ring(anchor, orientation, count, (x, y, z) =>
        {
            if (info is null) return z;
            float height = info.GetHeight(x, y, z);
            return float.IsFinite(height) && MathF.Abs(height - z) <= PlayerbotFleetPlacement.MaxStep ? height : z;
        }, static _ => true);
    }

    public string? Teleport(Guid botId, uint map, Vector3 position, float orientation)
    {
        if (bots.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player) return "bot-not-running";
        if (!player.IsAlive) return "bot-dead";
        if (bots.IsScripted(botId)) return "scripted";
        if (!bots.AllowsHomeMap(map)) return "map-not-allowed";
        if (services.GetService<TeleportFeature>() is not { } feature) return "teleport-unavailable";
        TeleportService teleports = feature.Teleports;
        if (teleports.IsBeingTeleported(player)) return "teleporting";
        if (!teleports.CanTeleportTo(player, map, position.X, position.Y, position.Z, orientation)) return "teleport-refused";
        return teleports.TeleportTo(player, map, position.X, position.Y, position.Z, orientation) ? null : "teleport-refused";
    }

    public string? SetDefend(Guid botId, PlayerbotDefendPost? post)
    {
        IReadOnlyList<PlayerbotOperationResult> results = bots.SetDefend([botId], post);
        return results.Count > 0 && results[0].Success ? null : results.Count > 0 ? results[0].Code : "bot-not-running";
    }

    private PlayerbotGroupCoordinator? Coordinator => services.GetService<PlayerbotGroupCoordinator>();

    public int? RunningSiegeId => Coordinator?.Siege?.Id;

    public IReadOnlyCollection<Guid> SiegeMembers()
        => Coordinator?.Siege is { } siege ? [.. siege.Raids.SelectMany(r => r.Group?.Members.Select(m => m.BotId) ?? [])] : [];

    public string? SiegeOutcome => Coordinator?.SiegeOutcome;

    public ScenarioSiegeResult StartSiege(IReadOnlyList<Guid> botIds, EventScenarioSiege siege)
    {
        if (Coordinator is not { } coordinator) return new ScenarioSiegeResult(false, 0, "groups-unavailable", 0, 0);
        // The stored waypoints (a GM may have staged some) are replaced only now, when this scenario is ready to start its own siege, and never
        // while another siege runs.
        if (coordinator.Siege is not null || coordinator.ClearSiegeWaypoints() < 0) return new ScenarioSiegeResult(false, 0, "siege-running", 0, 0);
        foreach (Vector3 waypoint in siege.Waypoints)
            if (coordinator.AddSiegeWaypoint(siege.Map, waypoint) is { } refusal)
                return new ScenarioSiegeResult(false, 0, "waypoint-" + refusal, 0, 0);
        var skipped = new List<(Guid BotId, string Reason)>();
        PlayerbotSiege? started = coordinator.StartSiege(botIds, siege.ObjectiveEntry, siege.Raids, skipped, out string? why);
        if (started is null) return new ScenarioSiegeResult(false, 0, why ?? "refused", 0, skipped.Count);
        return new ScenarioSiegeResult(true, started.Id, null, started.Raids.Sum(r => r.Group?.Members.Count ?? 0), skipped.Count);
    }

    public bool StopSiege(string reason) => Coordinator?.StopSiege(reason) ?? false;
}

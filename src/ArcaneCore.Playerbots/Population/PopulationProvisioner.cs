using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Combat;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// Gives a running bot its level and its place, as services and not as chat commands: the level and kit through
/// <see cref="CharacterBoost.Run"/> (the engine of <c>.character boost</c>; a bot is its own invoker, so it sends no GM message) and the
/// place through the teleport service (a bot acknowledges its own far and near teleports every tick, PlayerbotMovementControl).
/// <para>Thread affinity: <see cref="Provision"/>, <see cref="Relocate"/>, <see cref="IsSafe"/> and <see cref="Warm"/> run on the world
/// thread (callers use <c>world.InvokeAsync</c>). Every refusal is a code and changes nothing further; nothing throws for an expected refusal.</para>
/// <para>The safety check reads a <see cref="PopulationPlacementFacts"/> snapshot, which <see cref="Warm"/> builds on a background task and
/// posts back to the world thread: the spawn index and the terrain reads never run inside a tick. Only the world thread writes the warm
/// fields below; the background task touches nothing but its own locals and the interlocked counters.</para>
/// </summary>
internal sealed class PopulationProvisioner(IServiceProvider services)
{
    /// <summary>The refusal code for facts built from a creature content or area table that has since been replaced.</summary>
    internal const string StaleCode = "placement-data-stale";

    /// <summary>The refusal code for a bot something else drives now (a controller, its party, a group, the battlegrounds): it was autonomous when the pass began.</summary>
    internal const string HeldCode = "bot-held";

    /// <summary>The pass hold while the facts are not ready.</summary>
    internal const string WaitingReason = "waiting for the placement safety data";

    /// <summary>The suffix of <see cref="WaitingReason"/> while a failed build waits for its retry; a rush halts on it instead of holding.</summary>
    internal const string BuildFailedSuffix = " (the last build failed; retrying)";

    /// <summary>Passes between the retries of a failed facts build.</summary>
    internal const int WarmRetryPasses = 10;

    private bool _missingFactionsReported;

    // World-thread-only: the finished snapshot (and the atlas it covers) and the build in flight, if any.
    private PopulationPlacementFacts? _facts;
    private PopulationZoneAtlas? _factsAtlas;
    private CreatureContent? _warmContent;
    private AreaTable? _warmAreas;
    private PopulationZoneAtlas? _warmAtlas;
    private object? _warmToken;
    private bool _warmRunning;
    private bool _warmFailed;
    private int _warmFailedPasses;
    private CancellationTokenSource? _warmCts;
    private Task _warmTask = Task.CompletedTask;

    private int _factsBuilds;
    private int _unknownSiteRefusals;

    /// <summary>Test seam: awaited inside the background build before it starts, so a test can hold the build open.</summary>
    internal Func<CancellationToken, Task>? WarmGate { get; set; }

    /// <summary>Test seam: how many facts builds finished (any thread).</summary>
    internal int FactsBuilds => Volatile.Read(ref _factsBuilds);

    /// <summary>How many checks refused a location the facts do not cover (the facts and the atlas disagree; should stay 0).</summary>
    internal int UnknownSiteRefusals => Volatile.Read(ref _unknownSiteRefusals);

    /// <summary>World thread: the task of the latest build (completed when none ran); tests wait for it.</summary>
    internal Task WarmTask => _warmTask;

    /// <summary>World thread: the finished snapshot, or null before the first build. It may be stale; callers use it only after <see cref="Warm"/> returned null.</summary>
    internal PopulationPlacementFacts? Facts => _facts;

    /// <summary>Test seam: replaces the terrain area-level lookup (real terrain tiles are not available to unit tests).</summary>
    internal Func<WorldSafeLoc, int>? AreaLevelSource { get; set; }

    /// <summary>Test seam: supplies the liquid status at a location when terrain tiles are not loaded on disk.</summary>
    internal Func<WorldSafeLoc, LiquidStatus>? SurfaceSource { get; set; }

    /// <summary>Whether the boost content is loaded; when not, the missing piece is in <paramref name="missing"/> (world thread).</summary>
    public bool BoostAvailable(out string missing) => BoostEnvironment.Resolve(services, out missing) is not null;

    /// <summary>
    /// Boost the bot to <paramref name="level"/> when it is above its own, then teleport it to <paramref name="location"/>. Returns null on
    /// success, otherwise the refusal code (the bot is left as it was at the step that refused).
    /// </summary>
    public string? Provision(Guid botId, int level, WorldSafeLoc location, PopulationPlacementFacts facts)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (!IsCurrent(session.World, facts)) return StaleCode;
        // Check the planned level and this race's faction before CharacterBoost mutates the new character. Planning makes
        // the same check before creation; this guards a content reload between the plan and the final teleport.
        if (!IsSafe(facts, location, checked((byte)level), player.FactionTemplate))
        {
            string unsafeDetail = TryGetUnsafeSurfaceReason(facts, location, out string? surfaceReason)
                ? $"provision placement refused at safeLoc {location.Id} on map {location.MapId} ({location.X:F1},{location.Y:F1},{location.Z:F1}): {surfaceReason}"
                : $"provision placement refused at safeLoc {location.Id} on map {location.MapId} ({location.X:F1},{location.Y:F1},{location.Z:F1}): hostile spawns or area level unsafe for level {level}";
            PlayerbotAnomalies.Report(player, "population", "spawn-unsafe", unsafeDetail, AnomalySeverity.Warning);
            return "placement-unsafe";
        }
        if (level > player.Level)
        {
            string? refusal = !player.IsAlive ? "bot-dead"
                : player.Combat.IsInCombat ? "bot-in-combat"
                : !player.CanMutateQuestSettlementState ? "bot-settling-quest"
                : null;
            if (refusal is not null) return refusal;
            BoostEnvironment? environment = BoostEnvironment.Resolve(services, out string missing);
            if (environment is null) return "boost-unavailable: " + missing;
            var context = new CommandContext(session, player, services.GetRequiredService<CommandTable>());
            CharacterBoost.Run(context, player, (byte)level, environment);
        }

        return TeleportTo(player, location);
    }

    /// <summary>Whether the bot is currently on a taxi flight, ship, tram, elevator, or active travel leg and must not be relocated mid-transit.</summary>
    internal static bool IsInTransit(Player player)
        => PlayerbotTaxi.IsFlying(player)
            || PlayerbotTaxi.IsActive(player)
            || PlayerbotShip.IsActive(player)
            || PlayerbotTram.IsTraveling(player)
            || PlayerbotElevator.IsTraveling(player)
            || PlayerbotTravel.IsMidLeg(player);

    /// <summary>Teleport an idle bot to a graveyard (no boost). Null on success, otherwise the refusal code.</summary>
    public string? Relocate(Guid botId, WorldSafeLoc location, PopulationPlacementFacts facts)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (!player.IsAlive) return "bot-dead";
        if (player.Combat.IsInCombat) return "bot-in-combat";
        if (IsInTransit(player)) return "bot-in-transit";
        // The pass captured the bot as autonomous and has awaited the store since: a player may have invited it, an operator scripted it.
        if (services.GetService<ManagedPlayerbotFeature>()?.HeldFromBrain(botId) is not null) return HeldCode;
        if (!IsCurrent(session.World, facts)) return StaleCode;
        if (!IsSafe(facts, location, player.Level, player.FactionTemplate))
        {
            string unsafeDetail = TryGetUnsafeSurfaceReason(facts, location, out string? surfaceReason)
                ? $"relocate placement refused at safeLoc {location.Id} on map {location.MapId} ({location.X:F1},{location.Y:F1},{location.Z:F1}): {surfaceReason}"
                : $"relocate placement refused at safeLoc {location.Id} on map {location.MapId} ({location.X:F1},{location.Y:F1},{location.Z:F1}): hostile spawns or area level unsafe for level {player.Level}";
            PlayerbotAnomalies.Report(player, "population", "spawn-unsafe", unsafeDetail, AnomalySeverity.Warning);
            return "placement-unsafe";
        }

        return TeleportTo(player, location);
    }

    /// <summary>
    /// Whether the location is refused by the surface alone (non-finite coordinates, water), with the reason. The liquid status comes
    /// from <see cref="SurfaceSource"/> or from <paramref name="facts"/> (dry where the facts do not cover the location, or are null);
    /// it never reads a terrain tile.
    /// </summary>
    internal bool TryGetUnsafeSurfaceReason(PopulationPlacementFacts? facts, WorldSafeLoc location, out string? reason)
    {
        if (!float.IsFinite(location.X) || !float.IsFinite(location.Y) || !float.IsFinite(location.Z) || !float.IsFinite(location.Orientation))
        {
            reason = "non-finite coordinates";
            return true;
        }

        LiquidStatus liquid = LiquidStatus.NoWater;
        if (SurfaceSource is not null)
        {
            liquid = SurfaceSource(location);
        }
        else if (facts is not null && facts.Sites.TryGetValue(location, out PopulationPlacementSite site))
        {
            liquid = site.Liquid;
        }

        if (liquid is LiquidStatus.InWater or LiquidStatus.UnderWater)
        {
            reason = $"in water ({liquid})";
            return true;
        }

        reason = null;
        return false;
    }

    private CreatureContent CurrentContent() => services.GetService<CreatureWorldFeature>()?.Content ?? CreatureContent.Empty;

    private bool IsCurrent(WorldRuntime world, PopulationPlacementFacts facts)
        => facts.IsCurrentFor(CurrentContent(), WorldMaps.Of(world).Terrain.Areas);

    /// <summary>
    /// World thread: one safety check over the snapshot. It reads the spawn index, the liquid status and the area level from
    /// <paramref name="facts"/> (the two test seams are read on every call and win over it), so it never builds an index and never reads a
    /// terrain tile. A location the facts do not cover is unsafe (and counted: <see cref="UnknownSiteRefusals"/>).
    /// </summary>
    internal bool IsSafe(PopulationPlacementFacts facts, WorldSafeLoc location, byte level, uint faction)
    {
        if (TryGetUnsafeSurfaceReason(facts, location, out _)) return false;

        bool known = facts.Sites.TryGetValue(location, out PopulationPlacementSite site);
        if (!known && (SurfaceSource is null || AreaLevelSource is null))
        {
            Interlocked.Increment(ref _unknownSiteRefusals);
            return false;
        }

        int areaLevel = AreaLevelSource is not null ? AreaLevelSource(location) : site.AreaLevel;

        FactionTemplateCatalog factions = services.GetService<FactionTemplateCatalog>()
            ?? services.GetService<WorldCombatHooksFeature>()?.Catalog ?? FactionTemplateCatalog.Empty;
        if (factions.Count == 0 && !_missingFactionsReported)
        {
            _missingFactionsReported = true;
            services.GetService<ILoggerFactory>()?.CreateLogger<PopulationProvisioner>().LogWarning(
                "Playerbot population has no FactionTemplate catalog; unknown nearby creature reactions are treated as unsafe");
        }

        return PopulationPlacementSafety.IsSafe(location, level, faction, areaLevel, facts.Spawns, factions);
    }

    /// <summary>
    /// World thread, once per capture: null when the facts for the creature content, area table and <paramref name="atlas"/> in force are
    /// ready (read them from <see cref="Facts"/>), otherwise the reason the pass must not place anybody. Starts the background build when
    /// none is running for the current key, and restarts it when the key changes (the superseded build is cancelled and its late result
    /// ignored). A failed build is retried every <see cref="WarmRetryPasses"/> passes. There is no give-up limit: placing without the
    /// facts would put the index build and the tile reads back on the world thread.
    /// </summary>
    internal string? Warm(WorldRuntime world, PopulationZoneAtlas atlas, CancellationToken stop)
    {
        CreatureContent content = CurrentContent();
        TerrainManager terrain = WorldMaps.Of(world).Terrain;
        AreaTable areas = terrain.Areas;
        if (_facts is { } ready && ReferenceEquals(_factsAtlas, atlas) && ready.IsCurrentFor(content, areas)) return null;

        if (ReferenceEquals(_warmContent, content) && ReferenceEquals(_warmAreas, areas) && ReferenceEquals(_warmAtlas, atlas))
        {
            if (_warmRunning) return WaitingReason;
            if (_warmFailed && ++_warmFailedPasses < WarmRetryPasses) return WaitingReason + BuildFailedSuffix;
        }

        _ = CancelWarm();
        (_warmContent, _warmAreas, _warmAtlas) = (content, areas, atlas);
        var token = new object();
        _warmToken = token;
        _warmRunning = true;
        _warmFailed = false;
        _warmFailedPasses = 0;
        _warmCts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        CancellationToken ct = _warmCts.Token;
        Func<CancellationToken, Task>? gate = WarmGate;
        TerrainManager? readFrom = terrain.Enabled ? terrain : null;
        _warmTask = Task.Run(async () =>
        {
            PopulationPlacementFacts? built = null;
            try
            {
                if (gate is not null) await gate(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                WorldSafeLoc[] locations = [.. atlas.Zones.SelectMany(z => z.AllianceLocations.Concat(z.HordeLocations)).DistinctBy(l => l.Id)];
                built = PopulationPlacementFacts.Build(content, areas, readFrom, locations, ct);
                Interlocked.Increment(ref _factsBuilds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; } // superseded or stopping: nothing to deliver
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("playerbot.population", ex);
                AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"placement safety data build failed: {ex.GetType().Name}: {ex.Message}", origin: world.Identity);
                services.GetService<ILoggerFactory>()?.CreateLogger<PopulationProvisioner>().LogWarning(ex,
                    "Playerbot population: the placement safety data build failed; creating and relocating wait, and it is retried");
            }

            // Every outcome ends the run on the world thread, so a failed build is retried and never holds the state forever.
            world.Post(() =>
            {
                if (!ReferenceEquals(_warmToken, token)) return;
                _warmRunning = false;
                if (built is null) { _warmFailed = true; _warmFailedPasses = 0; return; }
                _facts = built;
                _factsAtlas = atlas;
            });
        }, CancellationToken.None);
        return WaitingReason;
    }

    /// <summary>Stop the build in flight (its late result is ignored); the task to wait for. Runs on the world thread; shutdown also calls it from another thread once the world has stopped and nothing else touches these fields.</summary>
    internal Task CancelWarm()
    {
        _warmToken = null;
        _warmRunning = false;
        _warmCts?.Cancel();
        _warmCts?.Dispose();
        _warmCts = null;
        return _warmTask;
    }

    private string? TeleportTo(Player player, WorldSafeLoc location)
    {
        if (services.GetService<TeleportFeature>() is not { } feature) return "teleport-unavailable";
        TeleportService teleports = feature.Teleports;
        if (!teleports.CanTeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation)) return "teleport-refused";
        return teleports.TeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation) ? null : "teleport-refused";
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
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
/// <para>Thread affinity: <see cref="Provision"/> and <see cref="Relocate"/> run on the world thread (callers use <c>world.InvokeAsync</c>).
/// Every refusal is a code and changes nothing further; nothing throws for an expected refusal.</para>
/// </summary>
internal sealed class PopulationProvisioner(IServiceProvider services)
{
    // World-thread-only caches: a pass invokes selection and teleports through WorldRuntime.InvokeAsync.
    private readonly Dictionary<uint, PlayerbotAreaLevelLookup> _areaLookups = [];
    private CreatureContent? _indexedContent;
    private PopulationSpawnIndex? _spawnIndex;
    private bool _missingFactionsReported;

    /// <summary>Test seam: replaces the terrain area-level lookup (real terrain tiles are not available to unit tests).</summary>
    internal Func<WorldSafeLoc, int>? AreaLevelSource { get; set; }

    /// <summary>Whether the boost content is loaded; when not, the missing piece is in <paramref name="missing"/> (world thread).</summary>
    public bool BoostAvailable(out string missing) => BoostEnvironment.Resolve(services, out missing) is not null;

    /// <summary>
    /// Boost the bot to <paramref name="level"/> when it is above its own, then teleport it to <paramref name="location"/>. Returns null on
    /// success, otherwise the refusal code (the bot is left as it was at the step that refused).
    /// </summary>
    public string? Provision(Guid botId, int level, WorldSafeLoc location)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        // Check the planned level and this race's faction before CharacterBoost mutates the new character. Planning makes
        // the same check before creation; this guards a content reload between the plan and the final teleport.
        if (!IsSafe(session.World, location, checked((byte)level), player.FactionTemplate)) return "placement-unsafe";
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

    /// <summary>Teleport an idle bot to a graveyard (no boost). Null on success, otherwise the refusal code.</summary>
    public string? Relocate(Guid botId, WorldSafeLoc location)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (!player.IsAlive) return "bot-dead";
        if (player.Combat.IsInCombat) return "bot-in-combat";
        if (!IsSafe(session.World, location, player.Level, player.FactionTemplate)) return "placement-unsafe";
        return TeleportTo(player, location);
    }

    /// <summary>World thread: one indexed safety check, with no per-teleport terrain dictionary or delegate.</summary>
    internal bool IsSafe(WorldRuntime world, WorldSafeLoc location, byte level, uint faction)
    {
        var terrain = WorldMaps.Of(world).Terrain;
        int areaLevel = 0;
        if (AreaLevelSource is not null) areaLevel = AreaLevelSource(location);
        else if (terrain.Enabled)
        {
            if (!_areaLookups.TryGetValue(location.MapId, out PlayerbotAreaLevelLookup? lookup))
                _areaLookups[location.MapId] = lookup = PlayerbotAreaLevelLookup.FromTerrain(terrain, location.MapId);
            areaLevel = lookup.LevelAt(location.X, location.Y, location.Z);
        }

        CreatureContent content = services.GetService<CreatureWorldFeature>()?.Content ?? CreatureContent.Empty;
        if (!ReferenceEquals(content, _indexedContent))
        {
            _spawnIndex = new PopulationSpawnIndex(content);
            _indexedContent = content;
        }

        FactionTemplateCatalog factions = services.GetService<FactionTemplateCatalog>()
            ?? services.GetService<WorldCombatHooksFeature>()?.Catalog ?? FactionTemplateCatalog.Empty;
        if (factions.Count == 0 && !_missingFactionsReported)
        {
            _missingFactionsReported = true;
            services.GetService<ILoggerFactory>()?.CreateLogger<PopulationProvisioner>().LogWarning(
                "Playerbot population has no FactionTemplate catalog; unknown nearby creature reactions are treated as unsafe");
        }

        return PopulationPlacementSafety.IsSafe(location, level, faction, areaLevel, _spawnIndex!, factions);
    }

    private string? TeleportTo(Player player, WorldSafeLoc location)
    {
        if (services.GetService<TeleportFeature>() is not { } feature) return "teleport-unavailable";
        TeleportService teleports = feature.Teleports;
        if (!teleports.CanTeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation)) return "teleport-refused";
        return teleports.TeleportTo(player, location.MapId, location.X, location.Y, location.Z, location.Orientation) ? null : "teleport-refused";
    }
}

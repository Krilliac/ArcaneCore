using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Character.Boost;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

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
    private string? _factionPath;
    private FactionTemplateCatalog? _factions;

    /// <summary>Whether the boost content is loaded; when not, the missing piece is in <paramref name="missing"/> (world thread).</summary>
    public bool BoostAvailable(out string missing) => BoostEnvironment.Resolve(services, out missing) is not null;

    /// <summary>
    /// Boost the bot to <paramref name="level"/> when it is above its own, then teleport it to <paramref name="location"/>. Returns null on
    /// success, otherwise the refusal code (the bot is left as it was at the step that refused).
    /// </summary>
    public string? Provision(Guid botId, int level, WorldSafeLoc location, IReadOnlyList<WorldSafeLoc> zoneLocations)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
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

        return TeleportTo(session, player, location, zoneLocations);
    }

    /// <summary>Teleport an idle bot to a graveyard (no boost). Null on success, otherwise the refusal code.</summary>
    public string? Relocate(Guid botId, WorldSafeLoc location, IReadOnlyList<WorldSafeLoc> zoneLocations)
    {
        if (services.GetService<ManagedPlayerbotFeature>()?.FindSession(botId) is not { } session || session.Player is not { IsInWorld: true } player)
            return "bot-not-running";
        if (!player.IsAlive) return "bot-dead";
        if (player.Combat.IsInCombat) return "bot-in-combat";
        return TeleportTo(session, player, location, zoneLocations);
    }

    private string? TeleportTo(WorldSession session, Player player, WorldSafeLoc location, IReadOnlyList<WorldSafeLoc> zoneLocations)
    {
        var terrain = WorldMaps.Of(session.World).Terrain;
        var areaLookups = new Dictionary<uint, PlayerbotAreaLevelLookup>();
        int AreaLevelAt(WorldSafeLoc at)
        {
            if (!terrain.Enabled) return 0;
            if (!areaLookups.TryGetValue(at.MapId, out PlayerbotAreaLevelLookup? lookup))
                areaLookups[at.MapId] = lookup = PlayerbotAreaLevelLookup.FromTerrain(terrain, at.MapId);
            return lookup.LevelAt(at.X, at.Y, at.Z);
        }

        CreatureWorldFeature? creatureFeature = services.GetService<CreatureWorldFeature>();
        WorldSafeLoc? safe = PopulationPlacementSafety.Choose(location, zoneLocations, player.Level, player.FactionTemplate,
            AreaLevelAt, creatureFeature?.Content ?? CreatureContent.Empty, Factions(creatureFeature));
        if (safe is null) return "placement-unsafe";
        if (services.GetService<TeleportFeature>() is not { } feature) return "teleport-unavailable";
        TeleportService teleports = feature.Teleports;
        if (!teleports.CanTeleportTo(player, safe.MapId, safe.X, safe.Y, safe.Z, safe.Orientation)) return "teleport-refused";
        return teleports.TeleportTo(player, safe.MapId, safe.X, safe.Y, safe.Z, safe.Orientation) ? null : "teleport-refused";
    }

    private FactionTemplateCatalog Factions(CreatureWorldFeature? feature)
    {
        if (services.GetService<FactionTemplateCatalog>() is { } registered) return registered;
        string? path = feature?.Options.FactionTemplateDbcPath;
        if (string.IsNullOrWhiteSpace(path)) return FactionTemplateCatalog.Empty;
        if (_factions is null || !string.Equals(_factionPath, path, StringComparison.Ordinal))
        {
            _factions = FactionTemplateDbcReader.Load(path);
            _factionPath = path;
        }

        return _factions;
    }
}

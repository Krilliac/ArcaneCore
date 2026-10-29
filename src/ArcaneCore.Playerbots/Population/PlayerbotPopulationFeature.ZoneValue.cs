using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Bots;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using GameItems = ArcaneCore.World.Items;

namespace ArcaneCore.World.Playerbots.Population;

public sealed partial class PlayerbotPopulationFeature
{
    private PopulationZoneValue? _zoneValue;
    private string _zoneValueKey = string.Empty;
    private object? _zoneValueToken;
    private object? _zoneValueAreas;
    private CancellationTokenSource? _zoneValueCts;
    private Task _zoneValueTask = Task.CompletedTask;
    private readonly PopulationZoneTrouble _trouble = new();
    private Guid _valueCursor = Guid.Empty;

    /// <summary>Test seam: replaces the production build of the zone content value (the content and terrain are then not needed). Set before the first pass.</summary>
    internal Func<CancellationToken, PopulationZoneValue?>? ZoneValueBuilder { get; set; }

    /// <summary>Test seam: part of the build key while <see cref="ZoneValueBuilder"/> is set; a new value is a source change.</summary>
    internal int ZoneValueBuilderVersion { get; set; }

    /// <summary>The latest zone value build task (tests).</summary>
    internal Task ZoneValueTask => _zoneValueTask;

    /// <summary>The zone content value in force; null until the first build is in (tests).</summary>
    internal PopulationZoneValue? ZoneValue => _zoneValue;

    /// <summary>World thread: stop the zone value build in flight; its late result is ignored.</summary>
    private void CancelZoneValue()
    {
        _zoneValueToken = null;
        _zoneValueKey = string.Empty; // a cancelled build restarts on the next capture
        _zoneValueCts?.Cancel();
        _zoneValueCts?.Dispose();
        _zoneValueCts = null;
    }

    /// <summary>
    /// World thread: start the background build of the zone content value (<see cref="PopulationZoneValue.Build"/>) when
    /// <c>ValueRouting</c> is on and the content it reads is loaded, and none is running or done for the current sources (area table,
    /// creature content and definitions version, quests, NPCs, loot, items, area triggers, on-kill reputation, faction templates, allowed
    /// maps). A change of any source (a content reload) rebuilds; nothing else does, so it never runs per think. The old value stays in
    /// force until the new one arrives, unless the area table changed. The zones are looked up from detached terrain tiles, spawns
    /// ordered by tile so one tile is held at a time, and nothing hops to the world thread. The result is posted back; it never holds
    /// creation back.
    /// </summary>
    private void EnsureZoneValue(WorldRuntime world, AreaTable areas, PlayerbotPopulationOptions pop)
    {
        if (!pop.ValueRouting) return;
        Func<CancellationToken, PopulationZoneValue?>? fake = ZoneValueBuilder;
        CreatureContent? creatures = _atlasCreatures as CreatureContent;
        WorldMaps maps = WorldMaps.Of(world);
        TerrainManager terrain = maps.Terrain;
        QuestNpcServices? questNpc = null;
        LootContent? loot = null;
        GameItems.ItemsFeature? itemsFeature = null;
        FactionTemplateCatalog? factions = null;
        ReputationService? reputation = null;
        if (fake is null)
        {
            questNpc = services.GetService<QuestNpcFeature>()?.Services;
            loot = services.GetService<GameObjectLootFeature>()?.LootContent;
            itemsFeature = services.GetService<GameItems.ItemsFeature>();
            factions = FactionTemplatesOf(world);
            if (creatures is null || !terrain.Enabled || questNpc is null || loot is null || itemsFeature is null || factions is not { Count: > 0 }) return;
            reputation = services.GetService<ReputationFeature>()?.Service;
        }

        // The area table changed (zone ids may mean something else): the old value no longer applies.
        if (!ReferenceEquals(_zoneValueAreas, areas))
        {
            _zoneValue = null;
            _zoneValueAreas = areas;
        }

        string allowed = string.Join(',', _options.AllowedMaps);
        string key = fake is not null
            ? "fake|" + ZoneValueBuilderVersion + "|" + RuntimeHelpers.GetHashCode(areas)
            : string.Create(CultureInfo.InvariantCulture,
                $"{allowed}|{creatures!.DefinitionsVersion}|{RuntimeHelpers.GetHashCode(areas)}|{RuntimeHelpers.GetHashCode(creatures)}"
                + $"|{RuntimeHelpers.GetHashCode(questNpc!.Quests)}|{RuntimeHelpers.GetHashCode(questNpc.Npcs)}|{RuntimeHelpers.GetHashCode(loot!)}"
                + $"|{RuntimeHelpers.GetHashCode(itemsFeature!.LoadedStore)}|{RuntimeHelpers.GetHashCode(maps.AreaTriggers)}"
                + $"|{RuntimeHelpers.GetHashCode(reputation?.OnKillEntries)}|{RuntimeHelpers.GetHashCode(factions!)}");
        if (key == _zoneValueKey) return;
        _zoneValueKey = key;
        _zoneValueCts?.Cancel();
        _zoneValueCts?.Dispose();
        var token = new object();
        _zoneValueToken = token;
        _zoneValueCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        CancellationToken ct = _zoneValueCts.Token;

        PopulationZoneValueSources? sources = null;
        if (fake is null)
        {
            HashSet<uint> gated = [];
            if (services.GetService<GameEventFeature>()?.Service?.Rows is { } rows)
                foreach ((int signedEvent, IReadOnlyList<uint> guids) in rows.Creatures)
                    if (signedEvent > 0) gated.UnionWith(guids);
            // Snapshots of what the world thread may change: the trigger, teleport and map tables.
            AreaTriggerTemplate[] triggers = [.. maps.AreaTriggers];
            var teleports = new Dictionary<uint, AreaTriggerTeleport>();
            var mapTemplates = new Dictionary<uint, MapTemplate>();
            foreach (AreaTriggerTemplate trigger in triggers)
            {
                if (maps.FindAreaTriggerTeleport(trigger.Id) is not { } teleport) continue;
                teleports[trigger.Id] = teleport;
                if (maps.Registry.Find(teleport.TargetMap) is { } target) mapTemplates[target.Entry] = target;
            }

            QuestStore quests = questNpc!.Quests;
            NpcStore npcs = questNpc.Npcs;
            IItemTemplateStore items = itemsFeature!.Templates;
            CreatureContent content = creatures!;
            sources = new PopulationZoneValueSources(
                [.. _options.AllowedMaps],
                content.GetSpawns,
                content.FindTemplate,
                id => quests.Get(id)?.Template,
                quests.StartersOf,
                loot!,
                items.Find,
                npcs.VendorItems,
                triggers,
                id => teleports.GetValueOrDefault(id),
                id => mapTemplates.GetValueOrDefault(id),
                reputation is null ? [] : [.. reputation.OnKillEntries],
                factions!,
                gated);
        }

        _zoneValueTask = Task.Factory.StartNew(() =>
        {
            PopulationZoneValue? result = null;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (fake is not null) result = fake(ct);
                else
                {
                    (uint Map, int X, int Y) loaded = (uint.MaxValue, 0, 0);
                    TerrainTile tile = TerrainTile.Empty;
                    result = PopulationZoneValue.Build(sources!, (map, x, y, _) =>
                    {
                        if (TerrainTile.TileOf(x, y) is not { } at) return 0;
                        if (loaded != (map, at.X, at.Y))
                        {
                            tile = terrain.ReadTileDetached(map, at.X, at.Y);
                            loaded = (map, at.X, at.Y);
                        }

                        ushort flag = tile.GetAreaFlag(x, y);
                        return flag == 0 ? 0 : areas.GetZoneAndAreaId(flag, map).ZoneId;
                    }, ct);
                }
            }
            catch (OperationCanceledException) { return; } // superseded or stopping: nothing to deliver
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AnomalyLedger.Shared.Report("bot-population", "pass-fault", "population", $"zone content value build failed: {ex.GetType().Name}: {ex.Message}", origin: _world?.Identity);
                logger.LogFault("playerbot.population", ex, "Playerbot population: the zone content value build failed; zones are chosen by level only until the sources change");
                result = null;
            }

            TimeSpan took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            world.Post(() =>
            {
                if (!ReferenceEquals(_zoneValueToken, token) || result is null) return;
                _zoneValue = result;
                LogZoneValue(result, took);
            });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>World thread: the operator-visible answer to "where are the good zones": how many zones have value and the best eight for a fresh level-capped bot of each team.</summary>
    private void LogZoneValue(PopulationZoneValue value, TimeSpan took)
    {
        PlayerbotPopulationOptions pop = _options.Population;
        PopulationValueWeights weights = PopulationValueWeights.From(pop);
        PopulationLevelRule rule = PopulationLevelRule.From(pop);
        var none = new Dictionary<uint, int>();
        logger.LogInformation("Playerbot population: zone content value in: {Zones} zones with value, built in {Seconds:0.0} s",
            value.Zones.Count, took.TotalSeconds);
        foreach (Team team in new[] { Team.Alliance, Team.Horde })
        {
            IReadOnlyList<ZoneScore> ranking = _atlas.Rank(PopulationBotProfile.Fresh(PlayerbotPopulationOptions.MaxLevel, team), rule, value, weights, none);
            string lines = string.Join("; ", ranking.Take(8).Select(s => string.Create(CultureInfo.InvariantCulture,
                $"{_atlas.Find(s.ZoneId)?.Name ?? "?"} ({s.ZoneId}) {s.Total:0.0} = Quest {s.Quest:0.0} Rep {s.Reputation:0.0} Gear {s.Gear:0.0} Farm {s.Farm:0.0} Elite -{s.Elite:0.0} Trouble -{s.Trouble:0.0} Distance -{s.Distance:0.0}")));
            logger.LogInformation("Playerbot population: best zones for a fresh level {Level} {Team} bot: {Zones}",
                PlayerbotPopulationOptions.MaxLevel, team, lines.Length == 0 ? "none valued" : lines);
        }
    }

    /// <summary>
    /// World thread: the profile the zone value measures a live bot against: its level, team, race, class, money and place, the quests it
    /// has rewarded or has in its log, its reputation ranks and the item level it wears per inventory type.
    /// </summary>
    private PopulationBotProfile ProfileOf(Player player)
    {
        QuestNpcServices? questNpc = services.GetService<QuestNpcFeature>()?.Services;
        PlayerQuestLog? log = questNpc?.StateOf(player)?.Quests;
        ReputationService? reputation = services.GetService<ReputationFeature>()?.Service;
        var worn = new Dictionary<uint, int>();
        return new PopulationBotProfile(player.Level, player.Team, (byte)player.Race, (byte)player.Class, player.Money, player.MapId, player.X, player.Y)
        {
            QuestDone = id => log is not null && questNpc!.Quests.Get(id) is { } quest && (log.RewardStatus(quest) || log.IsCurrent(id)),
            RankOf = faction => reputation is null ? ReputationRank.Neutral : reputation.GetRank(player, faction),
            EquippedItemLevel = inventoryType =>
            {
                if (!worn.TryGetValue(inventoryType, out int level)) worn[inventoryType] = level = EquippedItemLevelOf(player, inventoryType);
                return level;
            },
        };
    }

    /// <summary>
    /// The lowest item level worn in the equipment slots an inventory type fits (<see cref="ItemTemplateRules.AllowedEquipSlots"/>, the
    /// rule equipping uses), 0 when one of them is empty, and the highest value when the type fits no equipment slot (nothing to upgrade).
    /// </summary>
    internal static int EquippedItemLevelOf(Player player, uint inventoryType)
    {
        var shape = new ItemTemplate { InventoryType = inventoryType };
        int lowest = int.MaxValue;
        foreach (byte slot in shape.AllowedEquipSlots(player.Class, player.Inventory.Requirements.CanDualWield(player.Inventory)))
        {
            if (slot >= InventorySlots.EquipmentEnd) continue;
            int level = player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } worn ? (int)worn.Template.ItemLevel : 0;
            lowest = Math.Min(lowest, level);
        }

        return lowest;
    }
}

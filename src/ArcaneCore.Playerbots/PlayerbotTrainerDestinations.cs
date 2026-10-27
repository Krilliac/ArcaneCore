using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Bounded movement hints for class trainers which are not currently visible.</summary>
internal sealed class PlayerbotTrainerDestinations(WorldSession session, PlayerbotOptions options)
{
    private const int MaxEntryScan = 128;
    private const uint BackoffMs = 2_000;
    private const uint TrainerQuoteMemoMs = 5_000;
    private const int MaxQuoteMemos = 128;
    private static readonly ConditionalWeakTable<CreatureContent, TrainerRowCache> CachedTrainerRows = new();
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly List<Destination> _destinations = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private readonly Dictionary<uint, (uint Until, uint Level, uint Money, uint Limit)> _noTrainerOffer = [];

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this controller's plans.</summary>
    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private uint _routeSpawn;
    private Destination? _routeDestination;
    private uint _mapId;
    private int _contentVersion = -1;
    private int _eventStamp = -1;
    private CreatureContent? _content;
    private NpcTemplateMetadataLookup? _metadata;
    private bool _metadataRead;
    private uint _backoffUntil;
    private uint _stuckMs;
    private Vector3 _lastPosition;

    internal uint TargetEntry { get; private set; }

    /// <summary>Trainers a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

    /// <summary>What the bot may spend (<see cref="PlayerbotBudget"/>); null spends all its money.</summary>
    internal PlayerbotBudget? Budget { get; set; }

    internal bool HasCandidate(Player player)
    {
        if (!_options.Enabled || !player.IsInWorld || !player.IsAlive || player.Map is null
            || (player.Flags & PlayerFlags.Ghost) != 0 || player.Combat.IsInCombat || IsCasting(player))
            return false;
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services is null) return false;
        // A visible useful trainer belongs to TownGoals; this advisory method reports only
        // remote static hints that would justify this controller's movement.
        if (FindVisibleUsefulTrainer(player, services) is not null) return false;
        Refresh(player, player.Map!.MapId, services.Deps.Creatures as NpcTemplateMetadataLookup, services);
        return _destinations.Any(destination => PlayerbotWorldDestinations.IsPresent(player, destination.Spawn)
            && HasAffordableSpell(player, services, destination));
    }

    /// <summary>Returns true only when a normal managed movement step was attempted.</summary>
    internal bool Update(Player player, uint elapsedMs)
    {
        if (!_options.Enabled || elapsedMs == 0 || !player.IsInWorld || !player.IsAlive
            || (player.Flags & PlayerFlags.Ghost) != 0 || player.Combat.IsInCombat
            || IsCasting(player) || player.Map is not { } map)
            return false;

        uint now = _session.World.NowMs;
        if (_backoffUntil != 0)
        {
            if (unchecked(now - _backoffUntil) > int.MaxValue) return false;
            _backoffUntil = 0;
        }

        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services is null)
        {
            ClearRoute();
            _destinations.Clear();
            return false;
        }

        // A live lookup is authoritative. Static hints never interact and are discarded as soon
        // as the real creature/service comes into visibility.
        if (FindVisibleUsefulTrainer(player, services) is not null)
        {
            ClearRoute();
            TargetEntry = 0;
            return false;
        }

        Refresh(player, map.MapId, services.Deps.Creatures as NpcTemplateMetadataLookup, services);
        if (_route is not null)
        {
            if (_route.Complete) { ClearRoute(); }
            else
            {
                if (_routeDestination is null || !HasAffordableSpell(player, services, _routeDestination)
                    || !PlayerbotWorldDestinations.IsPresent(player, _routeDestination.Spawn))
                {
                    ClearRoute();
                    TargetEntry = 0;
                    return false;
                }
                Vector3 position = new(player.X, player.Y, player.Z);
                _stuckMs = Vector3.DistanceSquared(position, _lastPosition) < 0.0025f
                    ? _stuckMs + (PlayerbotMount.IsBusy(_session) ? 0 : elapsedMs) : 0;
                _lastPosition = position;
                if (_stuckMs >= 5_000)
                {
                    _blockedSpawns.Add(_routeSpawn);
                    ClearRoute();
                    Backoff(now);
                    return false;
                }
                if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now)) return true;
                _blockedSpawns.Add(_routeSpawn);
                ClearRoute();
                Backoff(now);
                return false;
            }
        }

        if (_destinations.Count > 0 && _destinations.All(d => _blockedSpawns.Contains(d.Spawn.Guid)))
        {
            _blockedSpawns.Clear();
            Backoff(now);
            return false;
        }

        foreach (Destination destination in _destinations
            .Where(d => !_blockedSpawns.Contains(d.Spawn.Guid) && PlayerbotWorldDestinations.IsPresent(player, d.Spawn))
            .OrderBy(d => DistanceSquared(player, d.Spawn)))
        {
            if (!HasAffordableSpell(player, services, destination)) continue;
            Vector3 target = new(destination.Spawn.X, destination.Spawn.Y, destination.Spawn.Z);
            PlayerbotRoute? walk = null;
            if (PlayerbotElevator.TryBegin(_session, player, target, _options, out walk))
            {
                ClearRoute();
                TargetEntry = destination.Entry;
                return true;
            }
            PlanOutcome planned = walk is not null
                ? PlanOutcome.Route
                : PlayerbotNavigation.PlanToward(player, target, _options, ref _tileWait, now, out walk);
            PlayerbotRoute? route = walk;
            if (planned == PlanOutcome.Pending)
            {
                // A tile of the way is still being read: not a verdict on this trainer. Keep it unblocked, plan again next think.
                TargetEntry = destination.Entry;
                return true;
            }
            if (planned == PlanOutcome.Refused)
            {
                _blockedSpawns.Add(destination.Spawn.Guid);
                return false;
            }

            _route = route;
            _routeSpawn = destination.Spawn.Guid;
            _routeDestination = destination;
            TargetEntry = destination.Entry;
            _lastPosition = new(player.X, player.Y, player.Z);
            _stuckMs = 0;
            if (PlayerbotNavigation.TryAdvance(_session, route!, _options, elapsedMs, now)) return true;
            _blockedSpawns.Add(destination.Spawn.Guid);
            ClearRoute();
            Backoff(now);
            return false;
        }

        Backoff(now);
        return false;
    }

    private void Refresh(Player player, uint mapId, NpcTemplateMetadataLookup? metadata, QuestNpcServices services)
    {
        CreatureContent? content = _session.Services.GetService<CreatureWorldFeature>()?.Content;
        if (content is null) { ClearRoute(); _destinations.Clear(); _noTrainerOffer.Clear(); return; }
        if (_metadataRead && _mapId == mapId && ReferenceEquals(_content, content)
            && _contentVersion == content.DefinitionsVersion && ReferenceEquals(_metadata, metadata)
            && _eventStamp == PlayerbotTownGoals.ActiveEventStamp(_session.Services)) return;

        ClearRoute();
        _mapId = mapId; _content = content; _contentVersion = content.DefinitionsVersion; _metadata = metadata; _metadataRead = true;
        _eventStamp = PlayerbotTownGoals.ActiveEventStamp(_session.Services);
        _destinations.Clear(); _blockedSpawns.Clear(); _noTrainerOffer.Clear(); TargetEntry = 0;
        // The trainer fields are the imported creature_template columns (CreatureNpcMetadataDataModule), with a configured
        // NpcServices:NpcTemplates row replacing an entry's. Filter class compatibility before the bounded spawn cap; unrelated
        // trainer rows must not crowd a valid class trainer out of the route index.
        foreach (NpcTemplateMetadata row in TrainerRows(content, metadata)
            .Where(row => row.TrainerType == TrainerType.Class && row.TrainerClass == (byte)player.Class)
            .Take(MaxEntryScan))
        {
            CreatureTemplate? template = content.FindTemplate(row.Entry);
            if (template is null || ((NpcFlags)template.NpcFlags & NpcFlags.Trainer) == 0) continue;
            FactionTemplateCatalog? factions = _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
            if (factions?.Find(template.Faction) is null || factions.Find(player.FactionTemplate) is null)
                continue;
            foreach (CreatureSpawn spawn in content.GetSpawns(mapId, row.Entry).Where(static s => s.IsFinite()).Where(spawn => PlayerbotWorldDestinations.IsPresent(player, spawn)).Take(MaxEntryScan - _destinations.Count))
                _destinations.Add(new(row.Entry, row.TrainerClass, factions!.Find(template.Faction)?.Faction ?? 0, template.Faction, spawn));
            if (_destinations.Count >= MaxEntryScan) break;
        }
        _destinations.Sort((a, b) => DistanceSquared(player, a.Spawn).CompareTo(DistanceSquared(player, b.Spawn)));
    }

    /// <summary>The service fields of every creature template in entry order, a configured override in place of the imported row.</summary>
    internal static IReadOnlyList<NpcTemplateMetadata> TrainerRows(CreatureContent content, NpcTemplateMetadataLookup? metadata)
    {
        TrainerRowCache cache = CachedTrainerRows.GetOrCreateValue(content);
        return cache.GetRows(content, metadata);
    }

    private sealed class TrainerRowCache
    {
        private readonly object _gate = new();
        private int _version = -1;
        private NpcTemplateMetadataLookup? _metadata;
        private NpcTemplateMetadata[]? _rows;

        public IReadOnlyList<NpcTemplateMetadata> GetRows(CreatureContent content, NpcTemplateMetadataLookup? metadata)
        {
            lock (_gate)
            {
                if (_rows is not null && _version == content.DefinitionsVersion && ReferenceEquals(_metadata, metadata))
                    return _rows;

                var list = new List<NpcTemplateMetadata>();
                foreach (CreatureTemplate template in content.Templates.OrderBy(t => t.Entry))
                {
                    list.Add(metadata?.MetadataFor(template.Entry) ?? new NpcTemplateMetadata
                    {
                        Entry = template.Entry,
                        GossipMenuId = template.GossipMenuId,
                        TrainerType = (TrainerType)template.TrainerType,
                        TrainerClass = template.TrainerClass,
                        TrainerRace = template.TrainerRace,
                        TrainerSpell = template.TrainerSpell,
                    });
                }

                NpcTemplateMetadata[] rows = [.. list];
                _version = content.DefinitionsVersion;
                _metadata = metadata;
                _rows = rows;
                return rows;
            }
        }
    }

    private bool HasAffordableSpell(Player player, QuestNpcServices services, Destination destination)
    {
        if (!PlayerbotWorldDestinations.IsPresent(player, destination.Spawn)) return false;
        uint now = _session.World.NowMs;
        if (Suspensions?.IsEntrySuspended(destination.Entry, now) == true) return false;
        if (Suspensions?.IsTrainingSuspended(now) == true) return false;
        if (!CanApproachTrainer(player, services, _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates, destination.FactionTemplate)) return false;
        uint? spendable = Budget?.Spendable(PlayerbotSpendKind.ClassTraining, player);
        uint limit = spendable ?? player.Money;
        if (_noTrainerOffer.TryGetValue(destination.Entry, out var memo)
            && unchecked((int)(memo.Until - now)) > 0
            && memo.Level == player.Level && memo.Money == player.Money && memo.Limit == limit)
        {
            return false;
        }

        NpcInfo hint = new(ObjectGuid.WithEntry(HighGuid.Unit, destination.Entry, destination.Spawn.Guid), destination.Entry,
            destination.Spawn.Guid, NpcFlags.Trainer, destination.Spawn.MapId, destination.Spawn.X, destination.Spawn.Y,
            destination.Spawn.Z, 0, true, false, false, false, 0,
            TrainerType.Class, destination.TrainerClass, 0, 0, destination.FactionId);
        if (services.GetClassTrainerQuote(player, hint, 0, spendable) is
            { TeachingSpell: not 0, LearnedSpell: not 0, Cost: var cost } && player.Money >= cost)
        {
            _noTrainerOffer.Remove(destination.Entry);
            return true;
        }

        if (_noTrainerOffer.Count >= MaxQuoteMemos) _noTrainerOffer.Clear();
        _noTrainerOffer[destination.Entry] = (unchecked(now + TrainerQuoteMemoMs), player.Level, player.Money, limit);
        return false;
    }

    private NpcInfo? FindVisibleUsefulTrainer(Player player, QuestNpcServices services)
    {
        if (services.Deps.Creatures is not { } lookup) return null;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            NpcInfo? npc = lookup.Find(player, guid, NpcFlags.Trainer);
            if (npc is { IsAlive: true, IsHostile: false, IsInCombat: false, IsNotSelectable: false }
                && (npc.NpcFlags & NpcFlags.Trainer) != 0
                && services.GetClassTrainerQuote(player, npc, 0, Budget?.Spendable(PlayerbotSpendKind.ClassTraining, player)) is not null)
                return npc;
        }
        return null;
    }

    /// <summary>
    /// Whether the bot may walk to a creature of faction template <paramref name="templateId"/>: its reputation reaction is above
    /// <paramref name="above"/> (<see cref="ReputationRank.Hostile"/> by default: only a creature that attacks on sight is refused), or
    /// without a reaction source its templates are not hostile (<paramref name="above"/> is ignored then: the templates cannot tell Unfriendly
    /// from Neutral). A service the bot will use (vendor, repair) passes
    /// <see cref="ReputationRank.Unfriendly"/>: vmangos <c>Player::CanInteractWithNPC</c> also refuses ranks up to Unfriendly
    /// (<see cref="QuestNpcServices.InteractableNpc(Player, ObjectGuid, NpcFlags)"/>), so an Unfriendly creature is walked to and then
    /// cannot be used (Timbermaw Hold vendors, issues #1478 and #1479).
    /// </summary>
    internal static bool CanApproachTrainer(Player player, QuestNpcServices services,
        FactionTemplateCatalog? factions, uint templateId, ReputationRank above = ReputationRank.Hostile)
    {
        if (factions?.Find(templateId) is not { } npc || factions.Find(player.FactionTemplate) is not { } self)
            return false;
        if (services.Deps.Reputation is INpcReactionSource reactions)
            return reactions.TryGetNpcReaction(player, npc, self, out ReputationRank reaction)
                && reaction > above;
        return factions.TryNpcHostility(templateId, player.FactionTemplate, out bool hostile) && !hostile;
    }

    private bool IsCasting(Player player)
        => _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting };

    private void ClearRoute() { _route = null; _routeSpawn = 0; _routeDestination = null; }
    private void Backoff(uint now) { _backoffUntil = unchecked(now + BackoffMs); }
    private static float DistanceSquared(Player p, CreatureSpawn s) => Vector3.DistanceSquared(new(p.X, p.Y, p.Z), new(s.X, s.Y, s.Z));
    private sealed record Destination(uint Entry, byte TrainerClass, uint FactionId, uint FactionTemplate, CreatureSpawn Spawn);
}

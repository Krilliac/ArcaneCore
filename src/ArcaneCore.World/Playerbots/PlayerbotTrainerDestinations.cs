using System.Numerics;
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
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly List<Destination> _destinations = [];
    private readonly HashSet<uint> _blockedSpawns = [];
    private PlayerbotRoute? _route;
    private uint _routeSpawn;
    private Destination? _routeDestination;
    private uint _mapId;
    private int _contentVersion = -1;
    private CreatureContent? _content;
    private NpcTemplateMetadataLookup? _metadata;
    private bool _metadataRead;
    private uint _backoffUntil;
    private uint _stuckMs;
    private Vector3 _lastPosition;

    internal uint TargetEntry { get; private set; }

    /// <summary>Trainers a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

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
        return _destinations.Any(destination => HasAffordableSpell(player, services, destination));
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
                if (_routeDestination is null || !HasAffordableSpell(player, services, _routeDestination))
                {
                    ClearRoute();
                    TargetEntry = 0;
                    return false;
                }
                Vector3 position = new(player.X, player.Y, player.Z);
                _stuckMs = Vector3.DistanceSquared(position, _lastPosition) < 0.0025f
                    ? _stuckMs + elapsedMs : 0;
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
            .Where(d => !_blockedSpawns.Contains(d.Spawn.Guid))
            .OrderBy(d => DistanceSquared(player, d.Spawn)))
        {
            if (!HasAffordableSpell(player, services, destination)) continue;
            Vector3 target = new(destination.Spawn.X, destination.Spawn.Y, destination.Spawn.Z);
            if (!PlayerbotNavigation.TryPlanToward(player, target, _options, out PlayerbotRoute? route))
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
        if (content is null) { ClearRoute(); _destinations.Clear(); return; }
        if (_metadataRead && _mapId == mapId && ReferenceEquals(_content, content)
            && _contentVersion == content.DefinitionsVersion && ReferenceEquals(_metadata, metadata)) return;

        ClearRoute();
        _mapId = mapId; _content = content; _contentVersion = content.DefinitionsVersion; _metadata = metadata; _metadataRead = true;
        _destinations.Clear(); _blockedSpawns.Clear(); TargetEntry = 0;
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
            foreach (CreatureSpawn spawn in content.GetSpawns(mapId, row.Entry).Where(IsFinite).Take(MaxEntryScan - _destinations.Count))
                _destinations.Add(new(row.Entry, row.TrainerClass, factions!.Find(template.Faction)?.Faction ?? 0, template.Faction, spawn));
            if (_destinations.Count >= MaxEntryScan) break;
        }
        _destinations.Sort((a, b) => DistanceSquared(player, a.Spawn).CompareTo(DistanceSquared(player, b.Spawn)));
    }

    /// <summary>The service fields of every creature template in entry order, a configured override in place of the imported row.</summary>
    private static IEnumerable<NpcTemplateMetadata> TrainerRows(CreatureContent content, NpcTemplateMetadataLookup? metadata)
    {
        foreach (CreatureTemplate template in content.Templates.OrderBy(t => t.Entry))
        {
            yield return metadata?.MetadataFor(template.Entry) ?? new NpcTemplateMetadata
            {
                Entry = template.Entry,
                GossipMenuId = template.GossipMenuId,
                TrainerType = (TrainerType)template.TrainerType,
                TrainerClass = template.TrainerClass,
                TrainerRace = template.TrainerRace,
                TrainerSpell = template.TrainerSpell,
            };
        }
    }

    private bool HasAffordableSpell(Player player, QuestNpcServices services, Destination destination)
    {
        if (Suspensions?.IsEntrySuspended(destination.Entry, _session.World.NowMs) == true) return false;
        if (Suspensions?.IsTrainingSuspended(_session.World.NowMs) == true) return false;
        if (!CanApproachTrainer(player, services, _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates, destination.FactionTemplate)) return false;
        NpcInfo hint = new(ObjectGuid.WithEntry(HighGuid.Unit, destination.Entry, destination.Spawn.Guid), destination.Entry,
            destination.Spawn.Guid, NpcFlags.Trainer, destination.Spawn.MapId, destination.Spawn.X, destination.Spawn.Y,
            destination.Spawn.Z, 0, true, false, false, false, 0,
            TrainerType.Class, destination.TrainerClass, 0, 0, destination.FactionId);
        return services.GetClassTrainerQuote(player, hint) is { TeachingSpell: not 0, LearnedSpell: not 0, Cost: var cost }
            && player.Money >= cost;
    }

    private static NpcInfo? FindVisibleUsefulTrainer(Player player, QuestNpcServices services)
    {
        if (services.Deps.Creatures is not { } lookup) return null;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            NpcInfo? npc = lookup.Find(player, guid);
            if (npc is { IsAlive: true, IsHostile: false, IsInCombat: false, IsNotSelectable: false }
                && (npc.NpcFlags & NpcFlags.Trainer) != 0
                && services.GetClassTrainerQuote(player, npc) is not null)
                return npc;
        }
        return null;
    }

    internal static bool CanApproachTrainer(Player player, QuestNpcServices services,
        FactionTemplateCatalog? factions, uint templateId)
    {
        if (factions?.Find(templateId) is not { } npc || factions.Find(player.FactionTemplate) is not { } self)
            return false;
        if (services.Deps.Reputation is INpcReactionSource reactions)
            return reactions.TryGetNpcReaction(player, npc, self, out ReputationRank reaction)
                && reaction > ReputationRank.Hostile;
        return factions.TryNpcHostility(templateId, player.FactionTemplate, out bool hostile) && !hostile;
    }

    private bool IsCasting(Player player)
        => _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting };

    private void ClearRoute() { _route = null; _routeSpawn = 0; _routeDestination = null; }
    private void Backoff(uint now) { _backoffUntil = unchecked(now + BackoffMs); }
    private static bool IsFinite(CreatureSpawn spawn) => float.IsFinite(spawn.X) && float.IsFinite(spawn.Y) && float.IsFinite(spawn.Z);
    private static float DistanceSquared(Player p, CreatureSpawn s) => Vector3.DistanceSquared(new(p.X, p.Y, p.Z), new(s.X, s.Y, s.Z));
    private sealed record Destination(uint Entry, byte TrainerClass, uint FactionId, uint FactionTemplate, CreatureSpawn Spawn);
}

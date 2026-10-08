using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The <c>creature_battleground</c> and <c>gameobject_battleground</c> rows by spawn guid and by event (vmangos
/// <c>BattleGroundMgr::m_CreatureBattleEventIndexMap</c>/<c>m_GameObjectBattleEventIndexMap</c> and <c>BattleGround::m_eventObjects</c>).
/// Read-only after startup.
/// </summary>
internal sealed class BattlegroundEventIndexMap
{
    private readonly Dictionary<uint, (byte E1, byte E2)[]> _creatureEvents;
    private readonly Dictionary<uint, (byte E1, byte E2)[]> _objectEvents;
    private readonly Dictionary<(byte, byte), uint[]> _creaturesOfEvent;
    private readonly Dictionary<(byte, byte), uint[]> _objectsOfEvent;

    private BattlegroundEventIndexMap(IReadOnlyList<BattlegroundEventIndex> creatures, IReadOnlyList<BattlegroundEventIndex> objects)
    {
        _creatureEvents = creatures.GroupBy(r => r.Guid).ToDictionary(g => g.Key, g => g.Select(r => (r.Event1, r.Event2)).ToArray());
        _objectEvents = objects.GroupBy(r => r.Guid).ToDictionary(g => g.Key, g => g.Select(r => (r.Event1, r.Event2)).ToArray());
        _creaturesOfEvent = creatures.GroupBy(r => (r.Event1, r.Event2)).ToDictionary(g => g.Key, g => g.Select(r => r.Guid).ToArray());
        _objectsOfEvent = objects.GroupBy(r => (r.Event1, r.Event2)).ToDictionary(g => g.Key, g => g.Select(r => r.Guid).ToArray());
    }

    public static BattlegroundEventIndexMap Empty { get; } = new([], []);

    public static BattlegroundEventIndexMap Build(BattlegroundContent content) => new(content.CreatureEvents, content.GameObjectEvents);

    public IEnumerable<uint> GatedCreatures => _creatureEvents.Keys;

    public IEnumerable<uint> GatedObjects => _objectEvents.Keys;

    /// <summary>The events of a creature spawn; empty when it belongs to none (vmangos <c>BG_EVENT_NONE</c>).</summary>
    public IReadOnlyList<(byte E1, byte E2)> CreatureEvents(uint guid) => _creatureEvents.GetValueOrDefault(guid) ?? [];

    public IReadOnlyList<(byte E1, byte E2)> ObjectEvents(uint guid) => _objectEvents.GetValueOrDefault(guid) ?? [];

    public IReadOnlyList<uint> CreaturesOf(byte event1, byte event2) => _creaturesOfEvent.GetValueOrDefault((event1, event2)) ?? [];

    public IReadOnlyList<uint> ObjectsOf(byte event1, byte event2) => _objectsOfEvent.GetValueOrDefault((event1, event2)) ?? [];
}

/// <summary>
/// The map resolver of battleground maps (installed as <see cref="Game.Instances.InstanceManager.BattlegroundMaps"/>): a participant enters its
/// match's map instance; nobody else enters one (vmangos MapManager::CreateBgMap and Player::TeleportTo); every arrival is reported so a far
/// teleport out of a match leaves it.
/// </summary>
internal sealed class BattlegroundMapResolver(BattlegroundFeature feature) : IMapResolver
{
    /// <summary>
    /// A login on a battleground map: the character hooks already moved a participant to its entry point (Player::LoadFromDB); one that got
    /// here anyway (no entry point known, a hook that did not run) is relocated to its bind point on the shared map of that place.
    /// </summary>
    public Map ResolveLoginMap(Player player)
    {
        if (feature.MatchOf(player.Guid) is { Map: { } map } && map.MapId == player.MapId)
        {
            return map;
        }

        if (feature.EntryPointOf(player.Guid) is { } point)
        {
            player.MapId = point.MapId;
            player.Relocate(point.X, point.Y, point.Z, point.Orientation, feature.World.NowMs);
        }

        return feature.World.MapResolver is { } resolver && !ReferenceEquals(resolver, this) && BattlegroundManager.TypeOfMap(player.MapId) == BattlegroundType.None
            ? resolver.ResolveLoginMap(player)
            : feature.World.GetMap(player.MapId);
    }

    public bool CanEnter(Player player, uint mapId) => feature.BattlegroundOf(player.Guid) is { } bg && bg.MapId == mapId;

    public Map? ResolveEntry(Player player, uint mapId)
        => feature.MatchOf(player.Guid) is { Map: { } map } && map.MapId == mapId ? map : null;

    public void OnEntered(Player player, Map map) => feature.OnPlayerEnteredMap(player, map);

    public Map? ResolveCorpseMap(uint mapId, uint instanceId)
        => feature.FindMatch(instanceId) is { Map: { } map } && map.MapId == mapId ? map : null;
}

/// <summary>The battleground matches as the death rules see them (corpse reclaim, Waiting to Resurrect on release).</summary>
internal sealed class BattlegroundDeathPresence(BattlegroundFeature feature) : IBattlegroundPresence
{
    public BattlegroundStatus? MatchStatusOf(ObjectGuid player) => feature.Manager.MatchStatusOf(player);

    /// <summary>vmangos Player::BuildPlayerRepop (Player.cpp:4586-4589): a participant gets Waiting to Resurrect (2584) before its ghost form.</summary>
    public void OnSpiritReleased(Player player)
    {
        if (feature.BattlegroundOf(player.Guid) is not null && feature.Services.GetService<SpellFeature>()?.System is { } spells)
        {
            spells.CastSpell(player, BattlegroundConstants.SpellWaitingToResurrect, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}

/// <summary>
/// The graveyard of a participant (vmangos Player::RepopAtGraveyard → <c>BattleGround::GetClosestGraveYard</c>, Player.cpp:4999-5004): the
/// match's choice by team and position; a player on a battleground map who is in no match is not handled.
/// </summary>
internal sealed class BattlegroundGraveyards(BattlegroundFeature feature) : IGraveyardOverride
{
    public bool TryChoose(Player player, out WorldSafeLoc? graveyard)
    {
        graveyard = null;
        if (feature.BattlegroundOf(player.Guid) is not { } bg || bg.PlayerTeam(player.Guid) is not { } team || player.MapId != bg.MapId)
        {
            return false;
        }

        GraveyardCatalog catalog = WorldGraveyards.Of(feature.World).Catalog;
        uint id = bg.ClosestGraveyard(team, player.X, player.Y, safeLoc => catalog.Find(safeLoc) is { } loc ? (loc.X, loc.Y) : null);
        graveyard = id == 0 ? null : catalog.Find(id);
        return true;
    }
}

/// <summary>The world states of a participant's match in its zone entry (vmangos Player::SendInitWorldStates → <c>FillInitialWorldStates</c>).</summary>
internal sealed class BattlegroundWorldStates(BattlegroundFeature feature) : IWorldStateProvider
{
    public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
    {
        if (feature.BattlegroundOf(player.Guid) is { } bg && player.MapId == bg.MapId)
        {
            foreach ((uint id, int value) in bg.InitialWorldStates())
            {
                states.Add(new WorldStatePair(id, value));
            }
        }
    }
}

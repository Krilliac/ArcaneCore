using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.OutdoorPvP;

/// <summary>The world side of the outdoor PvP scripts (<see cref="IOutdoorPvPHost"/>) over the continent maps' systems. World thread.</summary>
internal sealed class OutdoorPvPWorldHost(OutdoorPvPFeature feature) : IOutdoorPvPHost
{
    /// <summary>vmangos MAX_VISIBILITY_DISTANCE, the reach of DoSilithystYell's FindNearestCreature.</summary>
    private const float MaxVisibilityDistance = 250f;

    private readonly Dictionary<ObjectGuid, (Map Map, bool Creature)> _owned = [];

    private WorldRuntime World => feature.World;

    private Player? Online(ObjectGuid guid) => World.FindOnlinePlayer(guid);

    private Map? Continent(uint mapId) => World.FindMap(mapId, 0);

    public void SendWorldState(ObjectGuid player, uint state, uint value)
        => Online(player)?.Session.Send(WorldOpcode.SmsgUpdateWorldState, WorldStatePackets.BuildUpdate(state, value));

    public void CastOnSelf(ObjectGuid player, uint spellId)
    {
        if (Online(player) is { } p)
        {
            feature.Spells?.CastSpell(p, spellId, SpellCastTargets.ForSelf(), triggered: true);
        }
    }

    public void RemoveAura(ObjectGuid player, uint spellId)
    {
        if (Online(player) is { } p)
        {
            feature.Spells?.RemoveAuras(p, spellId);
        }
    }

    public bool HasAura(ObjectGuid player, uint spellId) => Online(player) is { } p && feature.Spells?.HasAura(p, spellId) == true;

    public IEnumerable<OutdoorPvPPlayer> ActivePlayersNear(uint mapId, float x, float y, float z, float radius)
    {
        if (Continent(mapId) is not { } map)
        {
            yield break;
        }

        float r2 = radius * radius;
        foreach (Player player in map.Players.ToArray())
        {
            float dx = player.X - x, dy = player.Y - y, dz = player.Z - z;
            if ((dx * dx) + (dy * dy) + (dz * dz) <= r2 && feature.IsOutdoorPvPActive(player))
            {
                yield return new OutdoorPvPPlayer(player.Guid, player.Team);
            }
        }
    }

    /// <summary>The type-29 template's data (vmangos GameObjectDefines.h:488-511); null without a usable template.</summary>
    public CapturePointTemplate? CapturePoint(uint entry)
    {
        foreach (Map map in World.Maps)
        {
            if (map.FindUpdater<GameObjectMapSystem>()?.FindTemplate(entry) is { } template)
            {
                if (template.Type != 29 || template.GetData(17) == 0)
                {
                    return null;
                }

                return new CapturePointTemplate(template.GetData(0), template.GetData(2), template.GetData(3), template.GetData(13),
                    template.GetData(12), template.GetData(16), template.GetData(17));
            }
        }

        return null;
    }

    public ObjectGuid? SummonObject(OutdoorPvPSpawn spawn, bool spawnedByDefault = false)
    {
        if (Continent(spawn.MapId) is not { } map || map.FindUpdater<GameObjectMapSystem>() is not { } objects
            || objects.Summon(spawn.Entry, spawn.X, spawn.Y, spawn.Z, spawn.Orientation) is not { } go)
        {
            return null;
        }

        go.SpawnedByDefault = spawnedByDefault;
        _owned[go.Guid] = (map, false);
        return go.Guid;
    }

    public void RemoveObject(ObjectGuid guid)
    {
        if (_owned.Remove(guid, out var owner) && owner.Map.FindUpdater<GameObjectMapSystem>() is { } objects && objects.Find(guid) is { } go)
        {
            objects.Remove(go);
        }
    }

    public void SetBannerArt(ObjectGuid guid, uint artKit, uint animation)
    {
        if (FindObject(guid) is not ({ } go, { } objects) || go.GetUInt32(UpdateFields.GameobjectArtkit) == artKit)
        {
            return;
        }

        go.SetUInt32(UpdateFields.GameobjectArtkit, artKit);
        objects.SendCustomAnim(go, animation);
    }

    public void PlayObjectSound(ObjectGuid guid, uint soundId)
    {
        if (FindObject(guid) is ({ } go, { } objects))
        {
            var w = new PacketWriter(4);
            w.WriteUInt32(soundId);
            objects.Map.BroadcastToObservers(go, WorldOpcode.SmsgPlaySound, w.ToArray());
        }
    }

    private (GameObject? Go, GameObjectMapSystem? Objects) FindObject(ObjectGuid guid)
        => _owned.TryGetValue(guid, out var owner) && owner.Map.FindUpdater<GameObjectMapSystem>() is { } objects
            ? (objects.Find(guid), objects)
            : (null, null);

    public ObjectGuid? SummonCreature(OutdoorPvPSpawn spawn, uint faction = 0, uint aura = 0)
    {
        if (Continent(spawn.MapId) is not { } map || map.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.SummonForInstance(spawn.Entry, spawn.X, spawn.Y, spawn.Z, spawn.Orientation) is not { } creature)
        {
            return null;
        }

        if (faction != 0)
        {
            creature.FactionTemplate = faction;
        }

        if (aura != 0)
        {
            creatures.AddAura(creature, aura, permanent: true);
        }

        _owned[creature.Guid] = (map, true);
        return creature.Guid;
    }

    public void RemoveCreature(ObjectGuid guid)
    {
        if (_owned.Remove(guid, out var owner) && owner.Map.FindUpdater<CreatureMapSystem>() is { } creatures && creatures.FindCreature(guid) is { } creature)
        {
            creatures.Despawn(creature);
        }
    }

    private const float AttackDistance = 5f; // vmangos ATTACK_DISTANCE

    /// <summary>member → leader, for the creature groups the scripts joined (<see cref="UpdateCreatureGroups"/>).</summary>
    private readonly Dictionary<ObjectGuid, ObjectGuid> _groups = [];

    private Creature? OwnedCreature(ObjectGuid guid)
        => _owned.TryGetValue(guid, out var owner) && owner.Creature && owner.Map.FindUpdater<CreatureMapSystem>() is { } creatures
            ? creatures.FindCreature(guid)
            : null;

    private CreatureMapSystem? SystemOf(ObjectGuid guid)
        => _owned.TryGetValue(guid, out var owner) ? owner.Map.FindUpdater<CreatureMapSystem>() : null;

    public void JoinCreatureGroup(ObjectGuid member, ObjectGuid leader)
    {
        if (OwnedCreature(member) is not { } m || OwnedCreature(leader) is not { } l)
        {
            return;
        }

        _groups[member] = leader;
        m.Motion.MoveFollow(l, AttackDistance, FollowAngle(l, m));
    }

    /// <summary>vmangos <c>leader-&gt;GetAngle(member) - member-&gt;GetOrientation()</c>, normalised to [0, 2pi).</summary>
    internal static float FollowAngle(WorldObject leader, WorldObject member)
    {
        float angle = MathF.Atan2(member.Y - leader.Y, member.X - leader.X) - member.Orientation;
        angle %= MathF.Tau;
        return angle < 0 ? angle + MathF.Tau : angle;
    }

    /// <summary>
    /// The group options each world tick: OPTION_AGGRO_TOGETHER (a member or the leader in a fight pulls the idle others onto its victim),
    /// OPTION_EVADE_TOGETHER (the leader evading sends the fighting members home too) and OPTION_FORMATION_MOVE (an idle member that lost
    /// its follow takes its slot again). Members whose leader is gone leave the group.
    /// </summary>
    internal void UpdateCreatureGroups()
    {
        foreach (ObjectGuid leaderGuid in _groups.Values.Distinct().ToArray())
        {
            ObjectGuid[] memberGuids = [.. _groups.Where(p => p.Value == leaderGuid).Select(p => p.Key)];
            if (OwnedCreature(leaderGuid) is not { IsAlive: true } leader)
            {
                foreach (ObjectGuid m in memberGuids) _groups.Remove(m);
                continue;
            }

            List<Creature> group = [leader];
            foreach (ObjectGuid m in memberGuids)
            {
                if (OwnedCreature(m) is { } member) group.Add(member);
                else _groups.Remove(m);
            }

            if (leader.IsInEvadeMode)
            {
                foreach (Creature member in group.Skip(1).Where(c => c.IsAlive && c.Combat.IsInCombat && !c.IsInEvadeMode))
                {
                    SystemOf(member.Guid)?.EnterEvadeMode(member);
                }

                continue;
            }

            Unit? enemy = group.FirstOrDefault(c => c.IsAlive && c.Combat.IsInCombat && c.Combat.Victim is { IsAlive: true })?.Combat.Victim;
            foreach (Creature c in group)
            {
                if (!c.IsAlive || c.Combat.IsInCombat || c.IsInEvadeMode) continue;
                if (enemy is not null && c.AI is { } ai)
                {
                    ai.AttackStart(enemy);
                }
                else if (!ReferenceEquals(c, leader) && c.Motion.CurrentType != MovementGeneratorType.Follow)
                {
                    c.Motion.MoveFollow(leader, AttackDistance, FollowAngle(leader, c));
                }
            }
        }
    }

    public bool StartSpecialPath(ObjectGuid creature, uint pathId)
    {
        if (OwnedCreature(creature) is not { } c || SystemOf(creature) is not { } creatures)
        {
            return false;
        }

        // The special path is keyed by its own id; cmangos-format data keeps the Spirit of Victory's walk as entry 18039 path 0.
        return c.Template.Entry == pathId && creatures.StartEntryWaypointPath(c, 0);
    }

    public void CreatureCastOnSelf(ObjectGuid creature, uint spellId)
    {
        if (_owned.TryGetValue(creature, out var owner) && owner.Map.FindUpdater<CreatureMapSystem>() is { } creatures && creatures.FindCreature(creature) is { } c)
        {
            creatures.CastSpell(c, spellId, c, triggered: false);
        }
    }

    private string? BroadcastText(uint id) => feature.Services.GetService<CreatureWorldFeature>()?.Content.Ai.BroadcastTexts.Find(id)?.Text;

    /// <summary>SMSG_DEFENSE_MESSAGE: u32 zone, u32 length (with the terminator), the text (vmangos Map.cpp:1869-1885).</summary>
    public void SendDefenseMessage(uint mapId, uint zoneId, uint broadcastTextId)
    {
        if (Continent(mapId) is not { } map || BroadcastText(broadcastTextId) is not { Length: > 0 } text)
        {
            return;
        }

        byte[] packet = BuildDefenseMessage(zoneId, text);
        foreach (Player player in map.Players.ToArray())
        {
            player.Session.Send(WorldOpcode.SmsgDefenseMessage, packet);
        }
    }

    internal static byte[] BuildDefenseMessage(uint zoneId, string text)
    {
        var w = new PacketWriter(9 + Encoding.UTF8.GetByteCount(text));
        w.WriteUInt32(zoneId);
        w.WriteUInt32((uint)Encoding.UTF8.GetByteCount(text) + 1);
        w.WriteCString(text);
        return w.ToArray();
    }

    public void SendZoneText(uint zoneId, string text)
    {
        byte[] packet = ChatPackets.BuildSystemMessage(text);
        foreach (Player player in World.OnlinePlayers.Where(p => p.ZoneId == zoneId).ToArray())
        {
            player.Session.Send(WorldOpcode.SmsgMessagechat, packet);
        }
    }

    public void NearestCreatureSays(ObjectGuid player, uint entry, uint broadcastTextId)
    {
        if (Online(player) is { Map: { } map } p && map.FindUpdater<CreatureMapSystem>() is { } creatures
            && creatures.CreaturesOfEntryInRange(p, entry, MaxVisibilityDistance).FirstOrDefault() is { } speaker)
        {
            creatures.SayText(speaker, (int)broadcastTextId);
        }
    }

    public void KilledMonsterCredit(ObjectGuid player, uint creatureEntry)
    {
        if (Online(player) is { } p)
        {
            feature.Services.GetService<QuestNpcFeature>()?.Services.KilledMonsterCredit(p, creatureEntry, default);
        }
    }

    public float? DistanceTo(ObjectGuid player, uint mapId, float x, float y, float z)
    {
        if (Online(player) is not { } p || p.MapId != mapId)
        {
            return null;
        }

        float dx = p.X - x, dy = p.Y - y, dz = p.Z - z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}

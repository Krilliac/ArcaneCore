using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Pets;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// The parts of the effects channel the Alterac Valley upgrades, landmines, shredders and yells need: the spawn modes of event creatures,
/// a creature's literal line, the event creature's yell, the removal of an event's objects, the charm a summoner holds, and the landmine
/// object script.
/// </summary>
internal sealed partial class MatchRuntime
{
    private AvLandmineAi? _landmines;
    private readonly HashSet<uint> _reportedYells = [];

    /// <summary>vmangos BattleGround::SetSpawnEventMode (BattleGround.cpp:1510-1533) over the event's creature spawns in this map.</summary>
    public void SetSpawnEventMode(byte event1, byte event2, BattlegroundSpawnMode mode)
    {
        if (_creatures is not { } creatures || Battleground is not { } battleground || event2 == BattlegroundConstants.EventNone)
        {
            return;
        }

        bool spawnMode = mode == BattlegroundSpawnMode.RespawnForced;
        foreach (uint guid in _feature.Events.CreaturesOf(event1, event2))
        {
            bool spawnThisCreature = _feature.Events.CreatureEvents(guid).All(e => battleground.IsActiveEvent(e.E1, e.E2));
            if (spawnThisCreature != spawnMode)
            {
                continue;
            }

            switch (mode)
            {
                case BattlegroundSpawnMode.RespawnForced or BattlegroundSpawnMode.RespawnStart:
                    creatures.SetEventRespawnMode(guid, forced: true);
                    break;
                case BattlegroundSpawnMode.RespawnStop:
                    creatures.SetEventRespawnMode(guid, forced: false);
                    break;
                case BattlegroundSpawnMode.DespawnForced:
                    if (creatures.Creatures.FirstOrDefault(c => c.Spawn?.Guid == guid) is { } gone)
                    {
                        creatures.SetEventRespawnMode(guid, forced: false);
                        creatures.Despawn(gone);
                    }

                    break;
            }
        }
    }

    /// <summary>vmangos MonsterSay / MonsterYell (LANG_UNIVERSAL, no target) of a creature of this map: 25 yd for a line, 300 yd for a yell.</summary>
    public void CreatureSay(ObjectGuid creature, string text, bool yell, ObjectGuid player)
    {
        if (Map?.FindObject(creature) is not Creature speaker)
        {
            return;
        }

        string line = text.Replace("%s", NameOf(player) ?? string.Empty, StringComparison.Ordinal);
        byte[] packet = CreatureChatPackets.BuildMonsterMessage(yell ? ChatType.MonsterYell : ChatType.MonsterSay, (uint)Language.Universal, speaker.Guid,
            speaker.Template.Name, ObjectGuid.Empty, line);
        Map.BroadcastInRange(speaker, yell ? CreatureChatPackets.YellRange : CreatureChatPackets.SayRange, WorldOpcode.SmsgMessagechat, packet, includeSelf: false);
    }

    /// <summary>
    /// vmangos SendYellToAll(textId, LANG_UNIVERSAL, GetSingleCreatureGuid(event1, 0)): the first creature of the event yells the mangos_string
    /// to every participant. The text must be known (<see cref="BattlegroundStrings"/>): vmangos would send its error text for a missing row,
    /// this server sends nothing and logs it once.
    /// </summary>
    public void EventCreatureYell(byte event1, uint textId)
    {
        if (_creatures is not { } creatures || _feature.Events.CreaturesOf(event1, 0) is not { Count: > 0 } spawns)
        {
            return;
        }

        Creature? source = creatures.Creatures.FirstOrDefault(c => c.Spawn?.Guid == spawns[0]);
        if (source is null)
        {
            return;
        }

        if (BattlegroundStrings.Mangos(textId) is not { } text)
        {
            if (_reportedYells.Add(textId))
            {
                _feature.Logger.LogWarning("battleground yell: mangos_string {Text} is unknown; the yell of event {Event} is not sent", textId, event1);
            }

            return;
        }

        SendToAll(WorldOpcode.SmsgMessagechat, CreatureChatPackets.BuildMonsterMessage(ChatType.MonsterYell, (uint)Language.Universal, source.Guid,
            source.Template.Name, ObjectGuid.Empty, text));
    }

    /// <summary>vmangos AddObjectToRemoveList over the event's game objects: they leave the map for good.</summary>
    public void RemoveEventGameObjects(byte event1, byte event2)
    {
        if (_gameObjects is not { } objects)
        {
            return;
        }

        foreach (uint guid in _feature.Events.ObjectsOf(event1, event2))
        {
            foreach (GameObject go in objects.GameObjects.Where(g => g.Spawn?.Guid == guid).ToArray())
            {
                objects.Remove(go);
            }
        }
    }

    /// <summary>The entry of the creature the player controls (Unit::GetCharm), 0 for none.</summary>
    public uint CharmedEntryOf(ObjectGuid player)
        => Online(player) is { } p && !p.CharmGuid.IsEmpty && p.Map?.FindObject(p.CharmGuid) is Creature charm ? charm.Entry : 0;

    /// <summary>Install the landmine script on the match's objects (Alterac Valley only).</summary>
    private void AttachAlteracValley(GameObjectMapSystem objects)
    {
        if (Type != BattlegroundType.AlteracValley)
        {
            return;
        }

        _landmines ??= new AvLandmineAi(this);
        objects.RegisterAi(AlteracValley.GameObjectLandmineHorde, _landmines);
        objects.RegisterAi(AlteracValley.GameObjectLandmineAlliance, _landmines);
    }

    private void DetachAlteracValley(GameObjectMapSystem objects)
    {
        if (_landmines is not null)
        {
            objects.UnregisterAi(AlteracValley.GameObjectLandmineHorde);
            objects.UnregisterAi(AlteracValley.GameObjectLandmineAlliance);
        }
    }

    /// <summary>
    /// The landmine object script (vmangos go_av_landmineAI, battleground_alterac.cpp:3870-3912): a despawned mine whose layer is dead (its
    /// event no longer (landmines, 0)) keeps putting its respawn off by urand(t/2, t) seconds of its spawn time; a mine only goes off for a
    /// hostile player, and then despawns. Hostile is a participant of the other team here (179324 is the Horde's mine, 179325 the
    /// Alliance's); vmangos asks the faction templates.
    /// </summary>
    private sealed class AvLandmineAi(MatchRuntime match) : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target)
        {
            Team mineTeam = go.Entry == AlteracValley.GameObjectLandmineHorde ? Team.Horde : Team.Alliance;
            if (target is not Player player || match.Battleground?.PlayerTeam(player.Guid) is not { } team || team == mineTeam)
            {
                return true; // "Do not attack friends!"
            }

            objects.DespawnForRespawn(go);
            return false; // the trap casts its spell
        }

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
            if (go.IsSpawned || match.Battleground is not { } battleground)
            {
                return;
            }

            byte eventIndex = go.Entry == AlteracValley.GameObjectLandmineHorde ? AlteracValley.EventLandminesHorde : AlteracValley.EventLandminesAlliance;
            if (!battleground.IsActiveEvent(eventIndex, 0))
            {
                uint respawn = GameObjectMapSystem.SpawnRespawnSeconds(go);
                if (respawn == 0)
                {
                    respawn = 180; // go_av_landmineAI's m_respawnTimer default
                }

                objects.SetRespawnIn(go, (uint)Random.Shared.Next((int)(respawn / 2), (int)respawn + 1));
            }
        }
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>
/// One match in the world: the effects channel of its <see cref="Battleground"/> (<see cref="IBattlegroundHost"/>: messages, sounds, world
/// states, statuses, resurrections, doors, spawns, teleports) and its map side (the spawn gate of the battleground events, the flag stand,
/// dropped flag and banner objects, the match's own objects such as the Arathi Basin buffs, and the battleground buff traps). World thread.
/// </summary>
internal sealed partial class MatchRuntime : IBattlegroundHost, IWrappingSpawnGate
{
    /// <summary>The group reward distance (vmangos CONFIG_FLOAT_GROUP_XP_DISTANCE, mangosd.conf MaxGroupXPDistance default 74).</summary>
    public const float GroupRewardDistance = 74f;

    /// <summary>A player further than this from its team's start is sent to a graveyard when the doors open (BattleGround.cpp:1462).</summary>
    public const float ReturnToStartDistance = 100f;

    private readonly BattlegroundFeature _feature;
    private readonly Dictionary<int, (uint Entry, float X, float Y, float Z, float O)> _objectDefinitions = [];
    private readonly Dictionary<int, ObjectGuid> _objects = [];
    private readonly List<(long DueMs, int Index)> _pendingObjects = [];
    private readonly List<(long DueMs, byte Event1, byte Event2)> _pendingEvents = [];
    private readonly Dictionary<ObjectGuid, long> _trapCooldowns = [];
    private GameObjectMapSystem? _gameObjects;
    private CreatureMapSystem? _creatures;
    private long _clockMs;

    public MatchRuntime(BattlegroundFeature feature, BattlegroundType type, uint instanceId)
    {
        _feature = feature;
        Type = type;
        InstanceId = instanceId;
    }

    public BattlegroundType Type { get; }

    public uint InstanceId { get; }

    /// <summary>The match (set right after the manager built it).</summary>
    public Battleground Battleground { get; set; } = null!;

    /// <summary>The match's map instance, once created.</summary>
    public Map? Map { get; private set; }

    /// <summary>The gate after this one's rule (the game-event gate).</summary>
    public ISpawnGate? Inner { get; set; }

    private WorldRuntime World => _feature.World;

    private SpellSystem? Spells => _feature.Services.GetService<SpellFeature>()?.System;

    private Player? Online(ObjectGuid guid) => World.FindOnlinePlayer(guid);

    private IEnumerable<Player> OnlineParticipants()
    {
        foreach ((ObjectGuid guid, _) in Battleground.Participants())
        {
            if (Online(guid) is { } player)
            {
                yield return player;
            }
        }
    }

    private void SendToAll(WorldOpcode opcode, byte[] payload)
    {
        foreach (Player player in OnlineParticipants())
        {
            player.Session.Send(opcode, payload);
        }
    }

    // ------------------------------------------------------------------ the map side

    /// <summary>Hook the map's systems (idempotent; the systems are retried every update until both exist).</summary>
    public void AttachMap(Map map)
    {
        Map = map;
        if (_gameObjects is null && map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            _gameObjects = objects;
            if (objects.SpawnGate is { } existing && !ReferenceEquals(existing, this))
            {
                Inner = existing;
            }

            objects.SpawnGate = this;
            objects.RegisterUseHandler(GameObjectType.FlagStand, UseFlagStand);
            objects.RegisterUseHandler(GameObjectType.FlagDrop, UseFlagDrop);
            objects.Used += OnObjectUsed;
            AttachAlteracValley(objects);
        }

        if (_creatures is null && map.FindUpdater<CreatureMapSystem>() is { } creatures)
        {
            _creatures = creatures;
            if (creatures.SpawnGate is { } existing && !ReferenceEquals(existing, this))
            {
                Inner ??= existing;
            }

            creatures.SpawnGate = this;
            AttachAlteracValley(creatures);
        }

        if (!_combatHooked)
        {
            map.Combat.UnitKilled += OnUnitKilled;
            _combatHooked = true;
        }
    }

    private bool _combatHooked;

    /// <summary>The match ended or the map unloads: let go of the map's systems.</summary>
    public void Detach()
    {
        if (_gameObjects is { } objects)
        {
            objects.Used -= OnObjectUsed;
            DetachAlteracValley(objects);
        }

        DetachAlteracValleyScripts();

        if (Map is { } map && _combatHooked)
        {
            map.Combat.UnitKilled -= OnUnitKilled;
            _combatHooked = false;
        }

        _gameObjects = null;
        _creatures = null;
    }

    public void Update(uint diffMs)
    {
        _clockMs += diffMs;
        if (Map is { } map && (_gameObjects is null || _creatures is null))
        {
            AttachMap(map);
        }

        foreach ((long due, int index) in _pendingObjects.Where(p => p.DueMs <= _clockMs).ToArray())
        {
            _pendingObjects.Remove((due, index));
            SummonMatchObject(index);
        }

        foreach ((long due, byte e1, byte e2) in _pendingEvents.Where(p => p.DueMs <= _clockMs).ToArray())
        {
            _pendingEvents.Remove((due, e1, e2));
            RefreshObjects(e1, e2);
        }

        UpdateBuffTraps();
    }

    // ---- the spawn gate (vmangos BattleGround::OnObjectDBLoad / CanBeSpawned) ----

    public IEnumerable<uint> GatedCreatures => _feature.Events.GatedCreatures.Concat(Inner?.GatedCreatures ?? []);

    public IEnumerable<uint> GatedGameObjects => _feature.Events.GatedObjects.Concat(Inner?.GatedGameObjects ?? []);

    /// <summary>A creature spawn of the match is in the world only while every event it belongs to is active (BattleGround.cpp:1337-1349).</summary>
    public bool AllowsCreature(uint spawnGuid)
        => _feature.Events.CreatureEvents(spawnGuid).All(e => Battleground is null || Battleground.IsActiveEvent(e.E1, e.E2))
            && (Inner?.AllowsCreature(spawnGuid) ?? true);

    /// <summary>A game object spawn of the match is in the world only while every event it belongs to is active (BattleGround.cpp:1380-1399).</summary>
    public bool AllowsGameObject(uint spawnGuid)
        => _feature.Events.ObjectEvents(spawnGuid).All(e => Battleground is null || Battleground.IsActiveEvent(e.E1, e.E2))
            && (Inner?.AllowsGameObject(spawnGuid) ?? true);

    private void RefreshObjects(byte event1, byte event2) => _gameObjects?.RefreshSpawns(_feature.Events.ObjectsOf(event1, event2));

    private void RefreshCreatures(byte event1, byte event2) => _creatures?.RefreshSpawns(_feature.Events.CreaturesOf(event1, event2));

    /// <summary>The object as the rules see it: its entry, its event pair and whether the player is within 10 yards.</summary>
    private BattlegroundObjectUse UseOf(Player player, GameObject go)
    {
        (byte e1, byte e2) = go.Spawn is { } spawn && _feature.Events.ObjectEvents(spawn.Guid) is { Count: > 0 } events
            ? events[0]
            : (BattlegroundConstants.EventNone, BattlegroundConstants.EventNone);
        float dx = go.X - player.X;
        float dy = go.Y - player.Y;
        float dz = go.Z - player.Z;
        return new BattlegroundObjectUse(go.Entry, e1, e2, (dx * dx) + (dy * dy) + (dz * dz) <= 10f * 10f);
    }

    /// <summary>vmangos <c>Player::CanUseBattleGroundObject</c> (Player.cpp:20448-20465): not a GM, not mounted, not in Spirit of Redemption.</summary>
    private bool CanUseBattlegroundObject(Player player)
        => !player.IsGameMaster && !MountService.IsMounted(player)
            && Spells?.HasAura(player, BattlegroundConstants.SpellSpiritOfRedemption) != true
            && Battleground.PlayerTeam(player.Guid) is not null;

    /// <summary>vmangos GameObject::Use, GAMEOBJECT_TYPE_FLAGSTAND (GameObject.cpp:1843-1870).</summary>
    private GameObjectUseResult UseFlagStand(Player player, GameObject go)
    {
        if (!CanUseBattlegroundObject(player))
        {
            return GameObjectUseResult.NotUsable;
        }

        Battleground.EventPlayerClickedOnFlag(player.Guid, UseOf(player, go));
        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// vmangos GameObject::Use, GAMEOBJECT_TYPE_FLAGDROP (GameObject.cpp:1885-1904) and the pickup spell's effect (SpellEffects.cpp:425-447):
    /// a Warsong Gulch dropped flag is handed to the battleground and deleted.
    /// </summary>
    private GameObjectUseResult UseFlagDrop(Player player, GameObject go)
    {
        if (!CanUseBattlegroundObject(player) || Type != BattlegroundType.WarsongGulch)
        {
            return GameObjectUseResult.NotUsable;
        }

        Battleground.EventPlayerClickedOnFlag(player.Guid, UseOf(player, go));
        _gameObjects?.Remove(go);
        return GameObjectUseResult.Ok;
    }

    /// <summary>
    /// vmangos Spell::EffectOpenLock (SpellEffects.cpp:2122-2133): opening an Arathi Basin or Alterac Valley banner (a button whose
    /// <c>noDamageImmune</c>, data4, is set) is a click on it.
    /// </summary>
    private void OnObjectUsed(Player player, GameObject go)
    {
        if (go.Type == GameObjectType.Button && go.Template.GetData(4) != 0
            && Type is BattlegroundType.ArathiBasin or BattlegroundType.AlteracValley && CanUseBattlegroundObject(player))
        {
            Battleground.EventPlayerClickedOnFlag(player.Guid, UseOf(player, go));
        }
    }

    // ---- the battleground buff traps (vmangos GameObject::Update, GameObject.cpp:476-551, and BattleGround::HandleTriggerBuff) ----

    private void UpdateBuffTraps()
    {
        if (_gameObjects is not { } objects || Map is not { } map || Battleground is null)
        {
            return;
        }

        foreach (GameObject go in objects.GameObjects.ToArray())
        {
            if (go.Type != GameObjectType.Trap || !go.IsSpawned || go.Template.GetData(2) != 0
                || go.Template.GetData(5) != BattlegroundConstants.BattlegroundTrapCooldownSeconds)
            {
                continue;
            }

            if (_trapCooldowns.TryGetValue(go.Guid, out long until) && until >= _clockMs)
            {
                continue;
            }

            Player? target = map.Players.FirstOrDefault(p => p.IsAlive && Within(go, p, BattlegroundConstants.BattlegroundTrapCooldownSeconds));
            if (target is null)
            {
                continue;
            }

            // The buff is the trap's spell on the player (the spell system cannot cast from an object, so the player casts it on itself).
            if (go.Template.GetData(3) is var spellId and not 0)
            {
                Spells?.CastSpell(target, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }

            _trapCooldowns[go.Guid] = _clockMs + (BattlegroundConstants.BattlegroundTrapCooldownSeconds * 1000L);

            // vmangos GameObject.cpp:546-552: the buff is cast on any living player in reach, but only a player who is in the battleground
            // (Player::InBattleGround) uses the object up; a game master or anyone else on the map leaves it where it is.
            if (Battleground.PlayerTeam(target.Guid) is null)
            {
                continue;
            }

            int index = _objects.FirstOrDefault(o => o.Value == go.Guid, new KeyValuePair<int, ObjectGuid>(-1, default)).Key;
            if (!Battleground.HandleTriggerBuff(index, go.Entry))
            {
                objects.Despawn(go);       // a static database buff respawns on its own timer (GO_JUST_DEACTIVATED)
            }
        }
    }

    private static bool Within(WorldObject a, WorldObject b, float range)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= range * range;
    }

    // ---- the match's own objects (vmangos m_bgObjects) ----

    public void AddBattlegroundObject(int index, uint entry, float x, float y, float z, float orientation)
        => _objectDefinitions[index] = (entry, x, y, z, orientation);

    public void SpawnBattlegroundObject(int index, uint respawnSeconds)
    {
        if (_objects.Remove(index, out ObjectGuid existing) && _gameObjects?.Find(existing) is { } live)
        {
            _gameObjects.Remove(live);
        }

        _pendingObjects.RemoveAll(p => p.Index == index);
        if (respawnSeconds == BattlegroundConstants.RespawnNeverSeconds)
        {
            return;
        }

        if (respawnSeconds == 0)
        {
            SummonMatchObject(index);
        }
        else
        {
            _pendingObjects.Add((_clockMs + (respawnSeconds * 1000L), index));
        }
    }

    private void SummonMatchObject(int index)
    {
        if (_gameObjects is { } objects && _objectDefinitions.TryGetValue(index, out var def)
            && objects.Summon(def.Entry, def.X, def.Y, def.Z, def.O) is { } go)
        {
            _objects[index] = go.Guid;
        }
    }

    /// <summary>
    /// The dropped flag (vmangos Spell::EffectSummonObjectWild, SpellEffects.cpp:3596-3666): the flag object at the dropper's feet, for the
    /// spell's duration, recorded as the match's dropped flag of the other team.
    /// </summary>
    public void SummonDroppedFlag(Player dropper, uint spellId)
    {
        if (_gameObjects is not { } objects || Battleground is not WarsongGulch wsg || Battleground.Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        uint entry = spellId == WarsongGulch.SpellWarsongFlagDropped ? WarsongGulch.HordeFlagGroundEntry : WarsongGulch.AllianceFlagGroundEntry;
        uint duration = Spells?.Store.Get(spellId) is { } info && info.GetDuration() is > 0 and var ms ? (uint)ms / 1000 : 0;
        if (objects.Summon(entry, dropper.X, dropper.Y, dropper.Z, dropper.Orientation, duration) is { } go)
        {
            Team flagTeam = dropper.Team == Team.Alliance ? Team.Horde : Team.Alliance;
            if (Battleground.PlayerTeam(dropper.Guid) is { } team)
            {
                flagTeam = BattlegroundConstants.OtherTeam(team);
            }

            wsg.SetDroppedFlagGuid(go.Guid, flagTeam);
        }
    }

    // ---- kills (vmangos Unit::Kill → BattleGround::HandleKillPlayer / HandleKillUnit, Unit.cpp:1272-1283) ----

    /// <summary>
    /// The player a kill is credited to (vmangos <c>pPlayerTap = GetCharmerOrOwnerPlayerOrPlayerItself()</c>, Unit.cpp:981): the killer itself
    /// when it is a player, else the player that charms or owns it (a pet, guardian, totem or charmed creature), else the player a stand-in
    /// unit reports through <see cref="IPlayerControlledUnit"/>. For a creature victim vmangos then prefers the creature's original loot
    /// recipient when player damage dominated (Unit.cpp:987-1001); creatures keep no such recipient here, so the controlling player stands.
    /// </summary>
    private static Player? CreditedPlayer(Unit? killer)
        => killer is null ? null : killer.GetCharmerOrOwnerPlayerOrSelf() ?? DuelRules.ControllingPlayer(killer);

    private void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (Battleground is null)
        {
            return;
        }

        Player? killerPlayer = CreditedPlayer(killer);
        if (victim is Player dead)
        {
            if (Battleground.PlayerTeam(dead.Guid) is not null)
            {
                Battleground.HandleKillPlayer(dead.Guid, killerPlayer?.Guid);
            }

            return;
        }

        if (victim is Creature creature && killerPlayer is not null && Battleground.PlayerTeam(killerPlayer.Guid) is not null)
        {
            byte event1 = creature.Spawn is { } spawn && _feature.Events.CreatureEvents(spawn.Guid) is { Count: > 0 } events
                ? events[0].E1
                : BattlegroundConstants.EventNone;
            Battleground.HandleKillUnit(creature.Entry, event1, killerPlayer.Guid);
        }
    }

    // ================================================================== IBattlegroundHost

    private string ResolveBroadcast(uint textId)
        => _feature.Services.GetService<CreatureWorldFeature>()?.Content.Ai.BroadcastTexts.Find(textId)?.Text
            ?? BattlegroundStrings.BroadcastFallback(textId)
            ?? $"[text {textId}]";

    private string? NameOf(ObjectGuid guid) => guid.IsEmpty ? null : Online(guid)?.Name;

    private static ChatType ChatOf(BattlegroundChatKind kind) => kind switch
    {
        BattlegroundChatKind.Alliance => ChatType.BgSystemAlliance,
        BattlegroundChatKind.Horde => ChatType.BgSystemHorde,
        _ => ChatType.BgSystemNeutral,
    };

    /// <summary>vmangos <c>SendMessageToAll(entry, type, source)</c>: the broadcast text in a battleground system message to every participant.</summary>
    public void Announce(uint textId, BattlegroundChatKind kind, ObjectGuid source)
    {
        string text = BattlegroundStrings.Format(ResolveBroadcast(textId), NameOf(source));
        SendToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildMessage(ChatOf(kind), Language.Universal, source, text, ChatTag.None));
    }

    /// <summary>vmangos <c>PSendMessageToAll(LANG_BATTLEGROUND_PREMATURE_FINISH_WARNING[_SECS], CHAT_MSG_SYSTEM)</c> (BattleGround.cpp:344,350).</summary>
    public void AnnouncePrematureFinish(uint amount, bool inMinutes)
    {
        string text = BattlegroundStrings.Format(BattlegroundStrings.Mangos(inMinutes ? 750u : 751u)!, null, amount);
        SendToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(text));
    }

    public void AnnounceFormatted(uint textId, BattlegroundChatKind kind, ObjectGuid source, uint arg1, uint arg2)
    {
        string template = BattlegroundStrings.Mangos(textId) ?? $"[string {textId}]";
        string text = BattlegroundStrings.Format(template, NameOf(source), BattlegroundStrings.Mangos(arg1) ?? string.Empty, BattlegroundStrings.Mangos(arg2) ?? string.Empty);
        SendToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildMessage(ChatOf(kind), Language.Universal, source, text, ChatTag.None));
    }

    /// <summary>
    /// vmangos <c>SendYell2ToAll(entry, LANG_UNIVERSAL, herald, arg1, arg2)</c>: a monster yell of the match's herald (the creature of the
    /// herald event) to every participant; without a herald in the map it goes out as a neutral battleground message.
    /// </summary>
    public void HeraldYell(uint textId, uint arg1, uint arg2)
    {
        string template = BattlegroundStrings.Mangos(textId) ?? $"[string {textId}]";
        string text = BattlegroundStrings.Format(template, null, BattlegroundStrings.Mangos(arg1) ?? string.Empty, BattlegroundStrings.Mangos(arg2) ?? string.Empty);
        Creature? herald = _creatures?.Creatures.FirstOrDefault(c => c.Spawn is { } s && _feature.Events.CreatureEvents(s.Guid).Any(e => e.E1 == AlteracValley.EventHerald));
        byte[] packet = herald is not null
            ? CreatureChatPackets.BuildMonsterMessage(ChatType.MonsterYell, (uint)Language.Universal, herald.Guid, herald.Template.Name, ObjectGuid.Empty, text)
            : ChatPackets.BuildMessage(ChatType.BgSystemNeutral, Language.Universal, ObjectGuid.Empty, text, ChatTag.None);
        SendToAll(WorldOpcode.SmsgMessagechat, packet);
    }

    public void PlaySoundToAll(uint soundId) => SendToAll(WorldOpcode.SmsgPlaySound, BattlegroundPackets.BuildPlaySound(soundId));

    public void UpdateWorldState(uint field, uint value) => SendToAll(WorldOpcode.SmsgUpdateWorldState, BattlegroundPackets.BuildUpdateWorldState(field, value));

    public void PlayerJoinedTeam(Team team, ObjectGuid joiner)
    {
        byte[] packet = BattlegroundPackets.BuildPlayerJoined(joiner);
        foreach (Player player in OnlineParticipants().Where(p => p.Guid != joiner && Battleground.PlayerTeam(p.Guid) == team))
        {
            player.Session.Send(WorldOpcode.SmsgBattlegroundPlayerJoined, packet);
        }
    }

    public void PlayerLeftTeam(Team team, ObjectGuid leaver)
    {
        byte[] packet = BattlegroundPackets.BuildPlayerLeft(leaver);
        foreach (Player player in OnlineParticipants().Where(p => p.Guid != leaver && Battleground.PlayerTeam(p.Guid) == team))
        {
            player.Session.Send(WorldOpcode.SmsgBattlegroundPlayerLeft, packet);
        }
    }

    public void SendStatus(ObjectGuid player, BattlegroundStatus status, uint time1, uint time2)
    {
        int slot = _feature.Manager.StateOf(player).SlotOf(Battleground.Template.QueueType);
        BattlegroundStatusSubject subject = BattlegroundStatusSubject.Of(Battleground);
        Online(player)?.Session.Send(WorldOpcode.SmsgBattlefieldStatus,
            BattlegroundPackets.BuildBattlefieldStatus((uint)Math.Max(slot, 0), subject.MapId, subject.Bracket, subject.ClientInstanceId, status, time1, time2));
    }

    /// <summary>vmangos EndBattleGround (BattleGround.cpp:716-725): WIN or LOSE, the final scoreboard, the IN_PROGRESS status with the leave timer.</summary>
    public void SendEndOfMatch(ObjectGuid player, bool won, PvpLogSnapshot finalScore, uint autoLeaveMs, uint startTimeMs)
    {
        if (Online(player) is not { } p)
        {
            return;
        }

        p.Session.Send(won ? WorldOpcode.SmsgBattlefieldWin : WorldOpcode.SmsgBattlefieldLose, won ? BattlegroundPackets.BuildBattlefieldWin() : BattlegroundPackets.BuildBattlefieldLose());
        p.Session.Send(WorldOpcode.MsgPvpLogData, BattlegroundPackets.BuildPvpLogData(finalScore));
        SendStatus(player, BattlegroundStatus.InProgress, autoLeaveMs, startTimeMs);
    }

    public void SendPvpLog(ObjectGuid player, PvpLogSnapshot log)
        => Online(player)?.Session.Send(WorldOpcode.MsgPvpLogData, BattlegroundPackets.BuildPvpLogData(log));

    public void ResurrectOrStopCombat(ObjectGuid player)
    {
        if (Online(player) is not { Map: { } map } p)
        {
            return;
        }

        if (!p.IsAlive)
        {
            map.Combat.ResurrectPlayer(p, 1.0f, applySickness: false);
            map.Combat.SpawnCorpseBones(p);
        }
        else
        {
            map.Combat.CombatStop(p);
        }
    }

    public void ResurrectIfDead(ObjectGuid player)
    {
        if (Online(player) is { Map: { } map } p && !p.IsAlive)
        {
            map.Combat.ResurrectPlayer(p, 1.0f, applySickness: false);
            map.Combat.SpawnCorpseBones(p);
        }
    }

    public void StopCombatWithPets(ObjectGuid player)
    {
        if (Online(player) is { Map: { } map } p)
        {
            map.Combat.CombatStop(p);
        }
    }

    /// <summary>vmangos <c>SetClientControl(player, 0)</c>: SMSG_CLIENT_CONTROL_UPDATE with the packed guid and 0.</summary>
    public void BlockMovement(ObjectGuid player)
    {
        if (Online(player) is { } p)
        {
            var writer = new PacketWriter(10);
            writer.WritePackedGuid(p.Guid.Value);
            writer.WriteByte(0);
            p.Session.Send(WorldOpcode.SmsgClientControlUpdate, writer.ToArray());
        }
    }

    public void MarkSkinnable(ObjectGuid player)
    {
        if (Online(player) is { } p)
        {
            p.UnitFlags |= UnitFlags.Skinnable;
        }
    }

    public bool IsAtGroupRewardDistance(ObjectGuid player, ObjectGuid victim)
        => Online(player) is { Map: { } map } p && Online(victim) is { } v && ReferenceEquals(v.Map, map) && Within(p, v, GroupRewardDistance);

    /// <summary>vmangos <c>ReturnPlayersToHomeGY</c> (BattleGround.cpp:1449-1467): further than 100 yards from the team's start, to a graveyard.</summary>
    public void ReturnToStartIfFar(ObjectGuid player, Team team)
    {
        if (Online(player) is not { } p || p.IsGameMaster)
        {
            return;
        }

        BattlegroundStartLocation start = team == Team.Alliance ? Battleground.Template.AllianceStart : Battleground.Template.HordeStart;
        float dx = p.X - start.X;
        float dy = p.Y - start.Y;
        float dz = p.Z - start.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > ReturnToStartDistance * ReturnToStartDistance)
        {
            DeathSeams.Find(World)?.Graveyards?.RepopAtGraveyard(p);
        }
    }

    /// <summary>vmangos <c>OpenDoorEvent(BG_EVENT_DOOR)</c> → <c>DoorOpen</c>: the doors of event (254, 0) go to the active state.</summary>
    public void OpenDoors()
    {
        if (_gameObjects is not { } objects)
        {
            return;
        }

        foreach (uint guid in _feature.Events.ObjectsOf(BattlegroundConstants.EventDoor, 0))
        {
            foreach (GameObject go in objects.GameObjects.Where(g => g.Spawn?.Guid == guid).ToArray())
            {
                go.State = GameObjectState.Active;
            }
        }
    }

    /// <summary>vmangos <c>StartingEventDespawnDoors</c>: the doors of event (254, 0) are removed.</summary>
    public void DespawnDoors()
    {
        if (_gameObjects is not { } objects)
        {
            return;
        }

        foreach (uint guid in _feature.Events.ObjectsOf(BattlegroundConstants.EventDoor, 0))
        {
            foreach (GameObject go in objects.GameObjects.Where(g => g.Spawn?.Guid == guid).ToArray())
            {
                objects.Remove(go);
            }
        }
    }

    public void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn) => EventStateChanged(event1, event2, spawn, forcedDespawn, 0);

    /// <summary>
    /// vmangos <c>SpawnEvent</c>'s object part (BattleGround.cpp:1469-1508): the creatures and game objects of the event are brought in line
    /// with the gate; a game object of an event that spawns with a delay appears after it.
    /// </summary>
    public void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn, uint respawnDelaySeconds)
    {
        RefreshCreatures(event1, event2);
        if (spawn && respawnDelaySeconds > 0)
        {
            _pendingEvents.Add((_clockMs + (respawnDelaySeconds * 1000L), event1, event2));
        }
        else
        {
            RefreshObjects(event1, event2);
        }
    }

    public void DeleteGameObject(ObjectGuid gameObject)
    {
        if (_gameObjects?.Find(gameObject) is { } go)
        {
            _gameObjects.Remove(go);
        }
    }

    /// <summary>vmangos <c>IsPointInAreaTriggerZone(atEntry, mapId, x, y, z, 2)</c> (BattleGroundWS.cpp:188).</summary>
    public bool IsInAreaTrigger(ObjectGuid player, uint areaTriggerId)
        => Online(player) is { } p && WorldMaps.Of(World).FindAreaTrigger(areaTriggerId) is { } trigger
            && AreaTriggerZone.Contains(trigger, p.MapId, p.X, p.Y, p.Z, 2.0f);

    public void LeaveBattleground(ObjectGuid player)
    {
        if (Online(player) is { } p)
        {
            _feature.LeaveBattleground(p, teleportToEntryPoint: true);
        }
    }

    public void ClearPlayerBinding(ObjectGuid player)
    {
        _feature.Manager.ClearBinding(player);
        _feature.DeleteBinding(player);
        if (Online(player) is { } p)
        {
            Spells?.RemoveAuras(p, BattlegroundConstants.SpellWaitingToResurrect);
        }
    }

    /// <summary>vmangos <c>TeleportToBGEntryPoint</c>: back to where the player queued, or to its bind point when that is unknown.</summary>
    public void TeleportToEntryPoint(ObjectGuid player)
    {
        if (Online(player) is not { } p || _feature.Services.GetService<TeleportFeature>() is not { } teleport)
        {
            return;
        }

        if (_feature.EntryPointOf(player) is { } point)
        {
            _feature.ForgetEntryPoint(player);
            if (teleport.Teleports.TeleportTo(p, point.MapId, point.X, point.Y, point.Z, point.Orientation))
            {
                return;
            }
        }

        teleport.Teleports.TeleportToHomebind(p);
    }

    public void UnhandledAreaTrigger(ObjectGuid player, uint areaTriggerId)
        => Online(player)?.Session.Send(WorldOpcode.SmsgAreaTriggerMessage,
            TeleportPackets.BuildAreaTriggerMessage($"Warning: Unhandled AreaTrigger in Battleground: {areaTriggerId}"));

    public void KilledMonsterCredit(ObjectGuid player, uint creatureEntry)
    {
        if (Online(player) is { } p && _feature.Services.GetService<QuestNpcFeature>() is { } quests)
        {
            quests.Services.KilledMonsterCredit(p, creatureEntry, ObjectGuid.Empty);
        }
    }

    /// <summary>vmangos <c>CompleteQuestForAll</c>: every participant with the quest in progress completes it (FullQuestComplete).</summary>
    public void CompleteQuestForAll(uint questId)
    {
        if (_feature.Services.GetService<QuestNpcFeature>() is not { } quests || quests.Services.Quests.Get(questId) is not { } quest)
        {
            return;
        }

        foreach (Player player in OnlineParticipants())
        {
            if (quests.Services.StateOf(player)?.Quests.Get(questId) is { Status: Game.Quests.QuestStatus.Incomplete })
            {
                quests.Services.GmCompleteQuest(player, quest);
            }
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Regthar Deathgate (entry 3389, the Barrens), "Counterattack!" (quest 4021): mangos-classic ScriptDev2 <c>npc_regthar_deathgate</c>
/// (kalimdor/the_barrens.cpp:821-1050, gossip and quest hooks :1057-1095, registered :1141-1147). Taking the quest, or asking "Where is Warlord
/// Krom'zar?" with it incomplete, starts the battle: seven Horde Defenders / Axe Throwers (the first five walk their entry path) and fifteen
/// Kolkar Invaders / Stormseers are summoned on fixed spots (corpse kept 40 s). A dead defender comes back on its spot 12 s later (1 s once more
/// than three are waiting), a dead Kolkar 10 s later; one of each per update. The twentieth Kolkar killed brings Warlord Krom'zar (9456), whose
/// banner the quest wants, with two guards; his death ends the battle: the yell, and every summon still there despawns (one minute later if
/// it is fighting).
/// </summary>
public sealed class RegtharDeathgateAI(Creature creature) : ScriptedAI(creature), IQuestScriptAI
{
    public const uint Entry = 3389, QuestCounterattack = 4021;
    public const uint NpcWarlordKromzar = 9456, NpcHordeDefender = 9457, NpcHordeAxeThrower = 9458, NpcKolkarInvader = 9524, NpcKolkarStormseer = 9523;
    public const int SayStart = -1010001, SayDefender = -1010002, YellRetreat = -1010003;
    public const uint KillLimit = 20, CorpseMs = 40_000, EventMs = 20 * 60_000, FightingDespawnMs = 60_000;
    public const uint HordeRespawnMs = 12_000, HordeRespawnFastMs = 1_000, KolkarRespawnMs = 10_000;

    /// <summary>The slots below this index of the Horde spots walk their entry's path (SpawnAllSlot/SpawnCreature moveToWP 5).</summary>
    public const int HordeWaypointSlots = 5;

    /// <summary>spawnPointsHorde (:784-793).</summary>
    public static readonly (float X, float Y, float Z, float O)[] HordeSpots =
    [
        (-307.23f, -1919.84f, 91.66f, 0.64f), (-281.15f, -1906.39f, 91.66f, 1.88f), (-270.09f, -1901.58f, 91.66f, 1.88f),
        (-275.24f, -1901.96f, 91.66f, 1.90f), (-295.84f, -1913.58f, 91.66f, 1.86f), (-221.61f, -1936.54f, 94.00f, 0.41f),
        (-226.65f, -1927.87f, 93.24f, 0.41f),
    ];

    /// <summary>spawnPointsKromzar (:795-800): an Invader, Krom'zar, a Stormseer.</summary>
    public static readonly (float X, float Y, float Z, float O)[] KromzarSpots =
    [
        (-281.19f, -1855.54f, 92.58f, 4.85f), (-283.66f, -1858.45f, 92.47f, 4.85f), (-286.50f, -1856.18f, 92.44f, 4.85f),
    ];

    /// <summary>spawnPointsKolkar (:802-819).</summary>
    public static readonly (float X, float Y, float Z, float O)[] KolkarSpots =
    [
        (-290.26f, -1860.85f, 92.48f, 3.68f), (-311.46f, -1871.16f, 92.64f, 6.06f), (-321.75f, -1868.48f, 93.73f, 0.30f),
        (-338.12f, -1852.10f, 94.09f, 0.01f), (-311.46f, -1847.78f, 94.93f, 6.15f), (-292.02f, -1840.00f, 93.06f, 3.51f),
        (-267.14f, -1853.97f, 93.24f, 3.52f), (-267.68f, -1832.77f, 92.69f, 3.52f), (-279.70f, -1845.67f, 92.81f, 3.83f),
        (-338.99f, -1868.68f, 93.50f, 0.17f), (-210.28f, -1916.22f, 92.87f, 4.89f), (-209.09f, -1922.44f, 93.26f, 3.87f),
        (-206.64f, -1924.83f, 93.78f, 2.41f), (-283.57f, -1883.91f, 92.60f, 3.53f), (-197.50f, -1929.31f, 94.03f, 3.51f),
    ];

    private readonly Dictionary<ObjectGuid, int> _hordeSlots = [];
    private readonly Dictionary<ObjectGuid, int> _kolkarSlots = [];
    private readonly List<(ObjectGuid Guid, long RespawnAtMs)> _killedHorde = [];
    private readonly List<(ObjectGuid Guid, long RespawnAtMs)> _killedKolkar = [];
    private long _clockMs;
    private uint _eventMs, _kolkarKills;
    private bool _eventStarted, _kromzarSpawned;

    public bool EventStarted => _eventStarted;

    public bool KromzarSpawned => _kromzarSpawned;

    public uint KolkarKills => _kolkarKills;

    /// <summary>Reset (:928-940).</summary>
    protected override void Reset()
    {
        _kolkarKills = 0;
        _eventMs = 0;
        _kromzarSpawned = false;
        _eventStarted = false;
        _killedKolkar.Clear();
        _killedHorde.Clear();
        _hordeSlots.Clear();
        _kolkarSlots.Clear();
    }

    /// <summary>JustRespawned (:942-946): the battle ends, then Reset.</summary>
    public override void OnRespawn()
    {
        FinishEvent();
        Reset();
    }

    /// <summary>QuestAccept_npc_regthar_deathgate (:1073-1083).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestCounterattack)
        {
            return;
        }

        System?.SayText(Me, SayStart, player);
        StartEvent();
    }

    /// <summary>GossipSelect_npc_regthar_deathgate (:1085-1095), GOSSIP_ACTION_INFO_DEF + 1.</summary>
    public void StartFromGossip(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        System?.SayText(Me, SayStart, player);
        StartEvent();
    }

    /// <summary>
    /// StartEvent (:948-959). Deviation: a run after an earlier one starts with Krom'zar not spawned and the stale slots and kill lists cleared;
    /// the source leaves <c>m_isKromzarSpawned</c> set after FinishEvent, so until Regthar resets no Kolkar returns and no Krom'zar comes again.
    /// </summary>
    public void StartEvent()
    {
        if (_eventStarted || System is not { } system)
        {
            return;
        }

        _kromzarSpawned = false;
        _kolkarKills = 0;
        _killedHorde.Clear();
        _killedKolkar.Clear();
        _hordeSlots.Clear();
        _kolkarSlots.Clear();

        _eventStarted = true;
        _eventMs = EventMs;
        SpawnAllSlots(system, HordeSpots, _hordeSlots, NpcHordeDefender, NpcHordeAxeThrower, HordeWaypointSlots);
        SpawnAllSlots(system, KolkarSpots, _kolkarSlots, NpcKolkarInvader, NpcKolkarStormseer, -1);
    }

    /// <summary>FinishEvent (:961-970): the yell, and every summon still there goes (a minute later while it fights).</summary>
    private void FinishEvent()
    {
        if (System is not { } system)
        {
            return;
        }

        system.SayText(Me, YellRetreat);
        ForceDespawn(system, _hordeSlots);
        ForceDespawn(system, _kolkarSlots);
        _eventStarted = false;
        _kolkarKills = 0;
    }

    private static void ForceDespawn(CreatureMapSystem system, Dictionary<ObjectGuid, int> slots)
    {
        foreach (ObjectGuid guid in slots.Keys)
        {
            if (system.FindCreature(guid) is { } creature)
            {
                system.ForcedDespawn(creature, creature.Combat.IsInCombat ? FightingDespawnMs : 0);
            }
        }
    }

    /// <summary>SpawnAllSlot (:887-905).</summary>
    private void SpawnAllSlots(CreatureMapSystem system, (float X, float Y, float Z, float O)[] spots, Dictionary<ObjectGuid, int> slots, uint entry1,
        uint entry2, int moveToWaypoint)
    {
        int counter = 0;
        foreach ((float x, float y, float z, float o) in spots)
        {
            uint entry = system.RandomInt(0, 1) != 0 ? entry1 : entry2;
            if (system.SummonCorpseTimedDespawn(Me, entry, x, y, z, o, null, CorpseMs) is { } summoned)
            {
                if (counter < moveToWaypoint)
                {
                    system.StartEntryWaypointPath(summoned, 0);
                }

                slots[summoned.Guid] = counter;
                counter++;
            }
        }
    }

    /// <summary>SpawnCreature (:850-884): the first killed creature of the list comes back on its spot once its time has passed.</summary>
    private void SpawnCreature(CreatureMapSystem system, (float X, float Y, float Z, float O)[] spots, Dictionary<ObjectGuid, int> slots,
        List<(ObjectGuid Guid, long RespawnAtMs)> killed, uint entry1, uint entry2, int moveToWaypoint)
    {
        if (killed.Count == 0)
        {
            return;
        }

        (ObjectGuid killedGuid, long respawnAtMs) = killed[0];
        if (!slots.TryGetValue(killedGuid, out int slot))
        {
            killed.RemoveAt(0);
            return;
        }

        if (respawnAtMs >= _clockMs)
        {
            return;
        }

        killed.RemoveAt(0);
        uint entry = system.RandomInt(0, 1) != 0 ? entry1 : entry2;
        (float x, float y, float z, float o) = spots[slot];
        if (system.SummonCorpseTimedDespawn(Me, entry, x, y, z, o, null, CorpseMs) is { } summoned)
        {
            if (slot < moveToWaypoint)
            {
                system.StartEntryWaypointPath(summoned, 0);
            }

            slots[summoned.Guid] = slot;
            slots.Remove(killedGuid);
        }
    }

    /// <summary>SummonedCreatureJustDied (:972-1020).</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        switch (summoned.Entry)
        {
            case NpcHordeDefender or NpcHordeAxeThrower:
                _killedHorde.Add((summoned.Guid, _clockMs + (_killedHorde.Count > 3 ? HordeRespawnFastMs : HordeRespawnMs)));
                if (System is { } system && system.RandomInt(0, 1) != 0)
                {
                    system.SayText(Me, SayDefender);
                }

                break;
            case NpcKolkarInvader or NpcKolkarStormseer:
                _killedKolkar.Add((summoned.Guid, _clockMs + KolkarRespawnMs));
                ++_kolkarKills;
                break;
            case NpcWarlordKromzar:
                FinishEvent();
                break;
        }
    }

    /// <summary>
    /// UpdateAI (:1022-1049). As in the source the twenty-minute limit cannot end the battle: its timer only runs once Krom'zar is out, when the
    /// check no longer applies. The respawn times are on a clock this AI keeps (the source reads the map's clock).
    /// </summary>
    public override void OnUpdate(uint diffMs)
    {
        _clockMs += diffMs;
        base.OnUpdate(diffMs);
        if (!_eventStarted || System is not { } system)
        {
            return;
        }

        if (!_kromzarSpawned && _eventMs < diffMs)
        {
            FinishEvent();
        }

        if (!_kromzarSpawned)
        {
            if (_kolkarKills >= KillLimit)
            {
                (uint Entry, int Spot)[] party = [(NpcKolkarInvader, 0), (NpcWarlordKromzar, 1), (NpcKolkarStormseer, 2)];
                foreach ((uint entry, int spot) in party)
                {
                    (float x, float y, float z, float o) = KromzarSpots[spot];
                    system.SummonCorpseTimedDespawn(Me, entry, x, y, z, o, null, CorpseMs);
                }

                _kromzarSpawned = true;
                return;
            }

            SpawnCreature(system, HordeSpots, _hordeSlots, _killedHorde, NpcHordeDefender, NpcHordeAxeThrower, HordeWaypointSlots);
            SpawnCreature(system, KolkarSpots, _kolkarSlots, _killedKolkar, NpcKolkarStormseer, NpcKolkarInvader, -1);
        }
        else
        {
            _eventMs = _eventMs > diffMs ? _eventMs - diffMs : 0;
        }
    }
}

/// <summary>
/// GossipHello/GossipSelect_npc_regthar_deathgate (the_barrens.cpp:1057-1071, :1085-1095): menu 2533 with his quests; with "Counterattack!"
/// incomplete the line "Where is Warlord Krom'zar?" starts the battle and closes the menu.
/// </summary>
public sealed class RegtharDeathgateGossip(Func<Player, uint, bool> questIncomplete) : INpcGossipScript
{
    public const uint TextMenu = 2533, ActionWhereIsKromzar = 1001, SenderMain = 1;
    public const string OptionWhereIsKromzar = "Where is Warlord Krom'zar?";

    public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        bool showQuests = (npc.NpcFlags & NpcFlags.QuestGiver) != 0;
        return questIncomplete(player, RegtharDeathgateAI.QuestCounterattack)
            ? new ScriptedGossipMenu(showQuests, TextMenu, [new ScriptedGossipItem(0, OptionWhereIsKromzar, SenderMain, ActionWhereIsKromzar)])
            : new ScriptedGossipMenu(showQuests, TextMenu, []);
    }

    public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;

    public ScriptedGossipReply SelectReply(Player player, NpcInfo npc, uint sender, uint action)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (action != ActionWhereIsKromzar)
        {
            return default;
        }

        if (player.Map?.FindUpdater<CreatureMapSystem>()?.FindCreature(npc.Guid)?.AI is RegtharDeathgateAI regthar)
        {
            regthar.StartFromGossip(player);
        }

        return new ScriptedGossipReply(0, Close: true);
    }
}

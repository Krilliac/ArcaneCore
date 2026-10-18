using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.World;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// cmangos <c>world/areatrigger_scripts.cpp</c>, slice 2: the five scripts that needed engine pieces first - at_murkdeep and at_hive_tower
/// (world map script data, <see cref="WorldMapData"/>), at_huldar_miran (temporary faction, <see cref="Creature.SetFactionTemporary"/>),
/// at_wondervolt (a trap used for a player, <see cref="GameObjectMapSystem.UseTrapFor"/>) and at_stormwind_recruiter (an EventAI custom event,
/// <see cref="CreatureMapSystem.SendAiEventToSelf"/>). Bound by the cmangos <c>scripted_areatrigger</c> ids, like slice 1.
/// </summary>
public sealed class AreaTriggerEngineScriptsFeature(IServiceProvider services) : IWorldFeature, IAreaTriggerListener
{
    public const uint TriggerHuldarMiran = 171, TriggerMurkdeep = 1966, TriggerStormwindRecruiter = 2746, TriggerHiveTower = 3146;
    public const uint TriggerWondervoltFirst = 4026, TriggerWondervoltLast = 4032;

    // at_huldar_miran
    public const uint QuestResupplyingTheExcavation = 273;
    public const uint NpcSaean = 1380, NpcMiran = 1379, NpcHuldar = 2057, NpcDarkIronAmbusher = 1981;
    public const uint FactionHostile = 14;
    /// <summary>SD2 SAY_MIRAN_AMBUSH (-1010029) is broadcast text 494, "Help! We're being attacked! Dark Irons!".</summary>
    public const int TextMiranAmbush = 494;
    public const uint AmbushCorpseMs = 25_000;
    public static readonly (float X, float Y, float Z, float O)[] MiranAmbushSpawns =
    [
        (-5760.73f, -3437.71f, 305.54f, 2.41f), (-5759.85f, -3441.29f, 305.57f, 2.24f), (-5757.75f, -3437.61f, 304.32f, 2.56f),
    ];

    // at_wondervolt
    public const uint GoWondervoltTrap = 180797;
    public static readonly uint[] WondervoltTransforms = [26272, 26157, 26273, 26274];

    // at_stormwind_recruiter
    public const uint NpcJustineDemalier = 12481, QuestTheFirstAndTheLast = 6182;

    public void Attach(WorldRuntime world) => ArgumentNullException.ThrowIfNull(world);

    public void OnAreaTrigger(Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (services.GetService<QuestNpcFeature>()?.Services is { } quests)
        {
            Run(quests, player, triggerId);
        }
    }

    /// <summary>Runs the script bound to <paramref name="triggerId"/>; returns what the cmangos script returns.</summary>
    public static bool Run(QuestNpcServices quests, Player player, uint triggerId)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(player);
        return triggerId switch
        {
            TriggerHuldarMiran => HuldarMiran(quests, player),
            TriggerMurkdeep => Murkdeep(quests, player),
            TriggerStormwindRecruiter => StormwindRecruiter(quests, player),
            TriggerHiveTower => HiveTower(player),
            >= TriggerWondervoltFirst and <= TriggerWondervoltLast => Wondervolt(player),
            _ => false,
        };
    }

    private static QuestStatusData? QuestOf(QuestNpcServices quests, Player player, uint questId)
        => quests.StateOf(player) is { Loaded: true } state && ReferenceEquals(state.Quests.Player, player) ? state.Quests.Get(questId) : null;

    private static QuestStatus StatusOf(QuestNpcServices quests, Player player, uint questId)
        => QuestOf(quests, player, questId)?.Status ?? QuestStatus.None;

    private static CreatureMapSystem? Creatures(Player player) => player.Map?.FindUpdater<CreatureMapSystem>();

    // AreaTrigger_at_murkdeep: on Wanted: Murkdeep, three Greymist Coastrunners come out of the water, unless Murkdeep is up or coastrunners
    // (alive or dead) are within 60 yd. The world map script runs the rest of the event.
    private static bool Murkdeep(QuestNpcServices quests, Player player)
    {
        if (!player.IsAlive || player.IsGameMaster || StatusOf(quests, player, KalimdorWorldMapData.QuestWantedMurkdeep) != QuestStatus.Incomplete)
        {
            return false;
        }

        if (player.Map?.FindUpdater<InstanceData>() is not KalimdorWorldMapData map || Creatures(player) is not { } creatures)
        {
            return false;
        }

        if (map.FindStoredCreature(KalimdorWorldMapData.NpcMurkdeep) is not null
            || creatures.ClosestOfEntry(player, KalimdorWorldMapData.NpcGreymistCoastrunner, 60f, aliveOnly: false) is not null)
        {
            return true;
        }

        map.SummonWave(KalimdorWorldMapData.NpcGreymistCoastrunner, KalimdorWorldMapData.NpcGreymistCoastrunner,
            KalimdorWorldMapData.NpcGreymistCoastrunner);
        return false;
    }

    // AreaTrigger_at_hive_tower: three Hive'Ashi Drones for 5 min, then not again until the world map script's 5 min timer resets TYPE_HIVE.
    private static bool HiveTower(Player player)
    {
        if (player.Map?.FindUpdater<InstanceData>() is not KalimdorWorldMapData map
            || map.GetData(KalimdorWorldMapData.TypeHive) != EncounterState.NotStarted)
        {
            return false;
        }

        if (!player.IsAlive || player.IsGameMaster || Creatures(player) is not { } creatures)
        {
            return false;
        }

        foreach ((float x, float y, float z, float o) in KalimdorWorldMapData.HiveDroneSpawns)
        {
            if (creatures.SummonDeadDespawnAt(KalimdorWorldMapData.NpcHiveAshiDrone, x, y, z, o) is { } drone)
            {
                creatures.MarkTimedDespawn(drone, 5 * 60_000); // TEMPSPAWN_TIMED_OR_DEAD_DESPAWN
            }
        }

        map.SetData(KalimdorWorldMapData.TypeHive, EncounterState.InProgress);
        return true;
    }

    // AreaTrigger_at_huldar_miran: on Resupplying the Excavation, Miran calls the ambush and the quest completes; unless Miran or Huldar
    // fight already, Saean turns hostile until he respawns (or is summoned so), and two Dark Iron Ambushers join him.
    // cmangos also requires a ScriptedMap (Eastern Kingdoms has one); this port has no Eastern Kingdoms world map script, which the
    // ambush does not use, so the check is left out.
    private static bool HuldarMiran(QuestNpcServices quests, Player player)
    {
        QuestStatus status = StatusOf(quests, player, QuestResupplyingTheExcavation);
        if (!player.IsAlive || player.IsGameMaster || status is QuestStatus.Complete or QuestStatus.None || Creatures(player) is not { } creatures)
        {
            return false;
        }

        Creature? miran = creatures.ClosestOfEntry(player, NpcMiran, 60f, aliveOnly: true);
        Creature? huldar = creatures.ClosestOfEntry(player, NpcHuldar, 60f, aliveOnly: true);
        Creature? saean = creatures.ClosestOfEntry(player, NpcSaean, 60f, aliveOnly: true);
        if (miran is null || huldar is null)
        {
            return false;
        }

        creatures.SayText(miran, TextMiranAmbush);
        quests.AreaExploredOrEventHappens(player, QuestResupplyingTheExcavation); // CompleteQuest + SendQuestCompleteEvent

        if (miran.Combat.IsInCombat || huldar.Combat.IsInCombat)
        {
            return true;
        }

        if (saean is null)
        {
            (float x, float y, float z, float o) = MiranAmbushSpawns[0];
            saean = creatures.SummonCorpseTimedDespawn(huldar, NpcSaean, x, y, z, o, null, AmbushCorpseMs);
        }

        saean?.SetFactionTemporary(FactionHostile, TemporaryFactionFlags.RestoreRespawn);

        if (saean is not null && creatures.ClosestOfEntry(player, NpcDarkIronAmbusher, 60f, aliveOnly: false) is null)
        {
            for (int i = 1; i <= 2; i++)
            {
                (float x, float y, float z, float o) = MiranAmbushSpawns[i];
                creatures.SummonCorpseTimedDespawn(saean, NpcDarkIronAmbusher, x, y, z, o, null, AmbushCorpseMs);
            }
        }

        return true;
    }

    // AreaTrigger_at_wondervolt: the PX-238 Winter Wondervolt within 20 yd is used for a player who is not already transformed.
    private static bool Wondervolt(Player player)
    {
        if (player.IsGameMaster || Creatures(player) is not { } creatures)
        {
            return false;
        }

        foreach (uint transform in WondervoltTransforms)
        {
            if (creatures.HasAura(player, transform))
            {
                return false;
            }
        }

        return player.Map?.FindUpdater<GameObjectMapSystem>() is { } objects
            && objects.ClosestOfEntry(player, GoWondervoltTrap, 20f) is { } trap
            && objects.UseTrapFor(trap, player);
    }

    // AreaTrigger_at_stormwind_recruiter: an Alliance player who has not done The First and the Last makes Justine Demalier (within 20 yd,
    // out of combat) hear AI_EVENT_CUSTOM_EVENTAI_A; her EventAI does the rest. The script returns false either way.
    private static bool StormwindRecruiter(QuestNpcServices quests, Player player)
    {
        if (player.IsGameMaster || player.Team != Team.Alliance || QuestOf(quests, player, QuestTheFirstAndTheLast) is { Rewarded: true }
            || Creatures(player) is not { } creatures)
        {
            return false;
        }

        if (creatures.ClosestOfEntry(player, NpcJustineDemalier, 20f, aliveOnly: true) is { } justine && !justine.Combat.IsInCombat)
        {
            creatures.SendAiEventToSelf(justine, AiEventTypes.CustomEventAiA, player);
        }

        return false;
    }
}

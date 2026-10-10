using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The Stranglethorn Vale Fishing Extravaganza judge, Riggle Bassbait: vmangos src/scripts/world/npcs_special.cpp:1250-1355
/// (npc_riggle_bassbaitAI and QuestRewarded_npc_riggle_bassbait). He offers Master Angler only while the tournament (game event 15) runs and
/// nobody has won it yet, yells the start and the end of the contest to the zone once each, and yells the winner's name when the first
/// angler is rewarded. The tournament pools and spawns themselves are ClassicDB game_event rows and are not touched here.
/// The vmangos saved variables (VAR_STV_FISHING_*) are saved in characters table world_stv_fishing whenever they change, as
/// sObjectMgr.SetSavedVariable(..., true) does, so a restart neither repeats a yell nor allows a second winner.
/// </summary>
public sealed class FishingExtravaganzaFeature(IServiceScopeFactory scopes, GameEventFeature events,
    ILogger<FishingExtravaganzaFeature>? logger = null) : IWorldFeature
{
    public const uint NpcRiggle = 15077;
    public const uint QuestMasterAngler = 8193;
    public const ushort EventTournament = 15;
    public const int YellBegin = 10608, YellOver = 10609, YellWinner = 10610;
    private const long Day = 24 * 60 * 60;

    private readonly HashSet<CreatureMapSystem> _installed = [];
    private QuestNpcServices? _hooked;

    /// <summary>VAR_STV_FISHING_ANNOUNCE_EVENT_BEGIN: the start yell is still owed.</summary>
    internal bool AnnounceBegin { get; set; } = true;

    /// <summary>VAR_STV_FISHING_ANNOUNCE_POOLS_DESPAN: the start was yelled, the end yell is owed once the pools are gone.</summary>
    internal bool AnnounceOver { get; set; }

    /// <summary>VAR_STV_FISHING_HAS_WINNER.</summary>
    internal bool HasWinner { get; set; }

    /// <summary>VAR_STV_FISHING_PREV_WIN_TIME (unix seconds).</summary>
    internal long PreviousWinTime { get; set; }

    internal Func<long> NowUnix { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal Func<bool> TournamentActive { get; set; } = () => false;

    public void Attach(WorldRuntime world)
    {
        TournamentActive = () => events.IsActiveEvent(EventTournament);
        Load();
        world.WorldTick += _ =>
        {
            HookQuestRewards();
            foreach (Map map in world.Maps.Where(m => m.MapId == 0))
            {
                if (map.FindUpdater<CreatureMapSystem>() is { } creatures && _installed.Add(creatures))
                    creatures.RegisterEntryAi(NpcRiggle, creature => new RiggleBassbaitAi(creature, this));
            }
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } system) _installed.Remove(system);
        };
    }

    /// <summary>The saved variables as they are now.</summary>
    internal FishingExtravaganzaState State => new(AnnounceBegin, AnnounceOver, HasWinner, PreviousWinTime);

    internal void Load()
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IFishingExtravaganzaStore>()?.LoadAsync().GetAwaiter().GetResult() is { } saved)
                (AnnounceBegin, AnnounceOver, HasWinner, PreviousWinTime) = saved;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "could not load the Fishing Extravaganza state");
        }
    }

    private void Save()
    {
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            scope.ServiceProvider.GetService<IFishingExtravaganzaStore>()?.SaveAsync(State).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "could not save the Fishing Extravaganza state");
        }
    }

    private void HookQuestRewards()
    {
        using IServiceScope scope = scopes.CreateScope();
        if (scope.ServiceProvider.GetService<QuestNpcFeature>()?.Services is not { } npcs || ReferenceEquals(npcs, _hooked)) return;
        if (_hooked is not null) _hooked.QuestRewarded -= OnQuestRewarded;
        npcs.QuestRewarded += OnQuestRewarded;
        _hooked = npcs;
    }

    /// <summary>npc_riggle_bassbaitAI constructor: a win older than a day resets the contest for the next tournament.</summary>
    internal void ResetIfStale()
    {
        if (NowUnix() - PreviousWinTime > Day)
        {
            AnnounceBegin = true;
            AnnounceOver = false;
            HasWinner = false;
            Save();
        }
    }

    /// <summary>QuestRewarded_npc_riggle_bassbait.</summary>
    internal void OnQuestRewarded(Player player, ObjectGuid questGiver, Quest quest)
    {
        if (quest.Id != QuestMasterAngler || player.Map?.FindObject(questGiver) is not Creature riggle || riggle.Entry != NpcRiggle) return;
        PreviousWinTime = NowUnix();
        HasWinner = true;
        Save();
        riggle.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        riggle.Map?.FindUpdater<CreatureMapSystem>()?.ZoneYell(riggle, YellWinner, player);
    }

    /// <summary>npc_riggle_bassbaitAI::CheckTournamentState, once a second.</summary>
    internal void CheckTournamentState(Creature riggle)
    {
        (bool questGiver, int yell) = Step();
        if (questGiver) riggle.NpcFlags |= (uint)NpcFlags.QuestGiver;
        else if ((riggle.NpcFlags & (uint)NpcFlags.QuestGiver) != 0) riggle.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        if (yell != 0) riggle.Map?.FindUpdater<CreatureMapSystem>()?.ZoneYell(riggle, yell);
    }

    /// <summary>The decision of CheckTournamentState: whether Riggle gives quests now, and the zone yell owed (0: none).</summary>
    internal (bool QuestGiver, int Yell) Step()
    {
        bool active = TournamentActive();
        if (active && !HasWinner)
        {
            if (!AnnounceBegin) return (true, 0);
            AnnounceBegin = false;
            AnnounceOver = true;
            Save();
            return (true, YellBegin);
        }

        // Only announce the end once the pools are gone (the tournament event is over).
        if (!active && AnnounceOver)
        {
            AnnounceOver = false;
            Save();
            return (false, YellOver);
        }

        return (false, 0);
    }
}

/// <summary>npc_riggle_bassbaitAI (vmangos npcs_special.cpp:1265-1335).</summary>
internal sealed class RiggleBassbaitAi : CreatureAI
{
    private readonly FishingExtravaganzaFeature _feature;
    private uint _timer;

    public RiggleBassbaitAi(Creature creature, FishingExtravaganzaFeature feature) : base(creature)
    {
        _feature = feature;
        feature.ResetIfStale();
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_timer < diffMs)
        {
            _feature.CheckTournamentState(Me);
            _timer = 1000;
        }
        else
            _timer -= diffMs;

        base.OnUpdate(diffMs);
    }
}

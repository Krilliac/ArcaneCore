using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// The world-thread honor owner: the per-player <see cref="HonorState"/> registry, <c>Add</c>/<c>Update</c>/
/// <c>Reset</c> and the client's honor tab fields. Behavioural port of vmangos <c>HonorMgr</c>
/// (HonorMgr.cpp:671-698, 792-1097). No reference code is copied. Also serves the other areas through
/// <see cref="IPlayerHonor"/> and <see cref="IHonorAwards"/>.
/// </summary>
public sealed class HonorService : IPlayerHonor, IHonorAwards
{
    private static readonly ConditionalWeakTable<ArcaneCore.Game.Spells.SpellSystem, HonorService> s_spellLinks = new();
    private readonly ConditionalWeakTable<Player, HonorState> _players = new();
    private readonly Func<uint> _weekBeginDay;
    private readonly IHonorSink? _sink;

    /// <param name="options">The configured options.</param>
    /// <param name="clock">The clock honor is dated with.</param>
    /// <param name="weekBeginDay">The first game day of the current honor week (the last maintenance day).</param>
    /// <param name="sink">Persistence; null keeps everything in memory (tests).</param>
    public HonorService(HonorOptions options, HonorClock clock, Func<uint> weekBeginDay, IHonorSink? sink = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _weekBeginDay = weekBeginDay ?? throw new ArgumentNullException(nameof(weekBeginDay));
        _sink = sink;
    }

    public HonorOptions Options { get; }

    public HonorClock Clock { get; }

    /// <summary>Let the honor spell effect (<see cref="HonorSpellEffects"/>) find this service from <paramref name="spells"/>.</summary>
    public void InstallForSpells(ArcaneCore.Game.Spells.SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        s_spellLinks.AddOrUpdate(spells, this);
    }

    /// <summary>The service installed for a spell system, or null.</summary>
    public static HonorService? ForSpells(ArcaneCore.Game.Spells.SpellSystem spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        return s_spellLinks.TryGetValue(spells, out HonorService? service) ? service : null;
    }

    /// <summary>World::m_gameDay with the configured time zone offset.</summary>
    public uint GameDay => Clock.GameDay(Options.TimeZoneOffsetHours * 3600);

    /// <summary>Build a player's initial state and publish its honor fields (login stage, Player.cpp:19135-19136).</summary>
    public HonorState Create(Player player, CharacterHonorData stored)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(stored);
        var state = new HonorState(stored);
        Refresh(player, state);
        return state;
    }

    public void Track(Player player, HonorState state)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(state);
        _players.AddOrUpdate(player, state);
    }

    public void Untrack(Player player) => _players.Remove(player);

    public HonorState? For(Player player) => _players.TryGetValue(player, out HonorState? state) ? state : null;

    byte IPlayerHonor.CurrentRank(Player player) => For(player)?.Rank.Rank ?? 0;

    byte IPlayerHonor.HighestRank(Player player) => For(player)?.HighestRank.Rank ?? 0;

    sbyte IPlayerHonor.VisualRank(Player player) => For(player)?.Rank.VisualRank ?? 0;

    /// <summary>creature_template.RacialLeader, minus the configured exclusions.</summary>
    public bool IsRacialLeader(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        return creature.Template.RacialLeader && !Options.RacialLeaderExcludedEntries.Contains(creature.Entry);
    }

    /// <summary>
    /// HonorMgr::Add: record contribution points (negative honor for a dishonorable kill), tell the client and
    /// persist. <paramref name="source"/> is the victim (a player, a creature) or null for honor from nowhere.
    /// </summary>
    public bool Add(Player player, float cp, HonorKind kind, Unit? source)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (cp == 0f || For(player) is not { } state)
        {
            return false;
        }

        Unit origin = source ?? player;
        var row = new HonorCpRecord(
            VictimType: ReferenceEquals(origin, player) ? (byte)0 : origin.TypeId,
            VictimId: origin is Player p ? p.Guid.Low : origin.GetUInt32(UpdateFields.ObjectFieldEntry),
            Cp: cp,
            Date: GameDay,
            Type: (byte)kind);
        float honor = kind == HonorKind.Dishonorable ? -cp : cp;
        byte highestBefore = state.HighestRank.Rank;
        float pointsBefore = state.RankPoints;

        if (kind == HonorKind.Dishonorable)
        {
            // DK penalties come off the rank points at once and are not part of the weekly adjustment (HonorMgr.cpp:827-833).
            state.RankPoints = state.RankPoints > cp ? state.RankPoints - cp : 0;
        }

        state.AddRow(row);
        _sink?.CpAdded(player, row);
        SendPvpCredit(player, source, honor);
        Refresh(player, state);
        if (state.RankPoints != pointsBefore || state.HighestRank.Rank != highestBefore)
        {
            _sink?.StateChanged(player, state.Snapshot());
        }

        return true;
    }

    /// <summary>HonorMgr::Update: recompute the derived values and write the honor tab fields.</summary>
    public void Update(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (For(player) is { } state)
        {
            Refresh(player, state);
        }
    }

    /// <summary>HonorMgr::Reset: forget everything (the PvP flag bits and City Protector title stay) and delete the rows.</summary>
    public void Reset(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (For(player) is not { } state)
        {
            return;
        }

        state.ClearRows();
        state.RankPoints = 0f;
        state.Standing = 0;
        state.LastWeekHk = 0;
        state.LastWeekCp = 0f;
        state.StoredHk = 0;
        state.StoredDk = 0;
        state.HighestRank = HonorRanks.None;
        _sink?.Reset(player);
        Refresh(player, state);
    }

    /// <summary>Set the rank points directly (GM command) and persist them.</summary>
    public void SetRankPoints(Player player, float rankPoints)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (For(player) is not { } state)
        {
            return;
        }

        state.RankPoints = float.IsFinite(rankPoints) ? rankPoints : 0f;
        Refresh(player, state);
        _sink?.StateChanged(player, state.Snapshot());
    }

    /// <summary>HonorMgr::CalculateTotalKills: how often <paramref name="killer"/> killed this exact victim today.</summary>
    public uint TotalKillsToday(Player killer, Unit victim)
    {
        ArgumentNullException.ThrowIfNull(killer);
        ArgumentNullException.ThrowIfNull(victim);
        if (For(killer) is not { } state || victim.TypeId is not (TypeId.Player or TypeId.Unit))
        {
            return 0;
        }

        uint id = victim is Player p ? p.Guid.Low : victim.GetUInt32(UpdateFields.ObjectFieldEntry);
        uint today = GameDay;
        uint total = 0;
        foreach (HonorCpRecord row in state.Rows)
        {
            if (row.VictimType == victim.TypeId && row.VictimId == id && row.Date == today)
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>HonorMgr::HonorableKillPoints: honor for <paramref name="killer"/> killing <paramref name="victim"/>.</summary>
    public float KillPoints(Player killer, Player victim, uint groupSize)
    {
        ArgumentNullException.ThrowIfNull(killer);
        ArgumentNullException.ThrowIfNull(victim);
        if (groupSize == 0)
        {
            return 0f;
        }

        return HonorKillPoints.Honorable(killer.Level, victim.Level, For(victim)?.Rank.VisualRank ?? 0, TotalKillsToday(killer, victim), groupSize);
    }

    // uint32(value > 0 ? value : 0) in the original; a non-finite total is 0 as well.
    private static uint ToField(float value) => float.IsFinite(value) && value > 0f ? (uint)value : 0u;

    /// <summary>HonorMgr::SendPVPCredit.</summary>
    private void SendPvpCredit(Player player, Unit? victim, float honor)
    {
        uint rank = 0;
        ulong guid = 0;
        if (victim is not null)
        {
            guid = victim.Guid.Value;
            if (victim is Creature creature)
            {
                if (IsRacialLeader(creature))
                {
                    rank = 19;
                }
            }
            else if (victim is Player other)
            {
                // Never display just "HK:" without a rank name: an unranked victim is sent as the first rank (Scout).
                rank = For(other)?.Rank.Rank ?? 0;
                if (rank == 0)
                {
                    rank = (uint)((HonorRanks.RankCount - HonorRanks.PositiveRankCount) + 1);
                }
            }
        }

        player.Session.Send(WorldOpcode.SmsgPvpCredit, HonorPackets.PvpCredit((int)honor, guid, rank));
    }

    /// <summary>The body of HonorMgr::Update (HonorMgr.cpp:843-940).</summary>
    private void Refresh(Player player, HonorState state)
    {
        uint today = GameDay;
        uint yesterday = today - 1;
        uint weekBegin = _weekBeginDay();
        uint todayHk = 0;
        uint todayDk = 0;
        uint yesterdayKills = 0;
        uint thisWeekKills = 0;
        float yesterdayCp = 0f;
        float thisWeekCp = 0f;
        int totalHk = state.StoredHk;
        int totalDk = state.StoredDk;

        foreach (HonorCpRecord row in state.Rows)
        {
            if (row.Type == (byte)HonorKind.Honorable)
            {
                if (row.Date == today)
                {
                    todayHk++;
                }

                if (row.Date == yesterday)
                {
                    yesterdayKills++;
                }

                if (row.Date >= weekBegin)
                {
                    thisWeekKills++;
                    totalHk++;
                }
            }

            if (row.Type != (byte)HonorKind.Dishonorable)
            {
                if (row.Date == yesterday)
                {
                    yesterdayCp += row.Cp;
                }

                if (row.Date >= weekBegin)
                {
                    thisWeekCp += row.Cp;
                }
            }
            else
            {
                if (row.Date == today)
                {
                    todayDk++;
                }

                totalDk++;
            }
        }

        state.TotalHk = totalHk;
        state.TotalDk = totalDk;
        state.Rank = HonorRanks.Calculate(state.RankPoints);
        if (state.Rank.VisualRank > 0 && state.Rank.VisualRank > state.HighestRank.VisualRank)
        {
            state.HighestRank = state.Rank;
        }

        player.SetByte(UpdateFields.PlayerFieldBytes, 3, state.HighestRank.Rank);
        player.SetByte(UpdateFields.PlayerBytes3, 3, state.Rank.Rank);
        player.SetByte(UpdateFields.PlayerFieldBytes2, 0, HonorRanks.RankBar(state.RankPoints, state.Rank));
        player.SetUInt16(UpdateFields.PlayerFieldSessionKills, 0, (ushort)todayHk);
        player.SetUInt16(UpdateFields.PlayerFieldSessionKills, 1, (ushort)todayDk);
        player.SetUInt32(UpdateFields.PlayerFieldYesterdayKills, yesterdayKills);
        player.SetUInt32(UpdateFields.PlayerFieldYesterdayContribution, ToField(yesterdayCp));
        player.SetUInt32(UpdateFields.PlayerFieldThisWeekKills, thisWeekKills);
        player.SetUInt32(UpdateFields.PlayerFieldThisWeekContribution, ToField(thisWeekCp));
        player.SetUInt32(UpdateFields.PlayerFieldLastWeekKills, state.LastWeekHk);
        player.SetUInt32(UpdateFields.PlayerFieldLastWeekContribution, ToField(state.LastWeekCp));
        player.SetUInt32(UpdateFields.PlayerFieldLastWeekRank, state.Standing);
        player.SetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills, (uint)totalHk);
        player.SetUInt32(UpdateFields.PlayerFieldLifetimeDishonorbaleKills, (uint)totalDk);
    }
}

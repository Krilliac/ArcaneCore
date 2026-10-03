using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// The world-thread reputation owner: per-player <see cref="PlayerReputation"/> registry,
/// client notifications, kill/quest rewards and reaction resolution. Also serves the NPC
/// services' <see cref="IPlayerReputation"/> seam.
/// </summary>
public sealed class ReputationService : IReputationService, IPlayerReputation, IQuestReputationSettlement
{
    private readonly ConditionalWeakTable<Player, PlayerReputation> _players = new();
    private readonly Dictionary<uint, ReputationOnKillEntry> _onKill;
    private readonly IReputationSink? _sink;
    private readonly Func<double> _roll;

    public ReputationService(FactionCatalog factions, IEnumerable<ReputationOnKillEntry>? onKill = null,
        ReputationRates? rates = null, IReputationSink? sink = null, Func<double>? roll = null)
    {
        ArgumentNullException.ThrowIfNull(factions);
        Factions = factions;
        _onKill = [];
        foreach (ReputationOnKillEntry entry in onKill ?? [])
        {
            if (!_onKill.TryAdd(entry.CreatureEntry, entry))
            {
                throw new ArgumentException($"duplicate kill reputation for creature {entry.CreatureEntry}", nameof(onKill));
            }
        }

        Rates = rates ?? new ReputationRates();
        _sink = sink;
        _roll = roll ?? Random.Shared.NextDouble;
    }

    public FactionCatalog Factions { get; }

    public ReputationRates Rates { get; }

    public int OnKillCount => _onKill.Count;

    /// <summary>
    /// Percentage points added to positive gains (SPELL_AURA_MOD_REPUTATION_GAIN, plus the
    /// faction-specific aura for kills). The spells owner supplies it; absent means none.
    /// </summary>
    public Func<Player, ReputationSource, uint, float>? GainModifier { get; set; }

    /// <summary>Option <c>Reputation:PeaceForcedUsesEffectiveStanding</c>, copied onto every state built by <see cref="Create"/>.</summary>
    public bool PeaceForcedUsesEffectiveStanding { get; init; }

    /// <summary>Build a player's initial state (session task, before the player enters the world).</summary>
    public PlayerReputation Create(Player player, CharacterReputationData stored)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(stored);
        var reputation = new PlayerReputation(Factions, player.Race, player.Class)
        {
            PeaceForcedUsesEffectiveStanding = PeaceForcedUsesEffectiveStanding,
        };
        reputation.Load(stored.Factions, stored.WatchedFaction);
        player.SetInt32(UpdateFields.PlayerFieldWatchedFactionIndex, reputation.WatchedFaction);
        return reputation;
    }

    public void Track(Player player, PlayerReputation reputation)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(reputation);
        _players.AddOrUpdate(player, reputation);
    }

    public void Untrack(Player player) => _players.Remove(player);

    public PlayerReputation? For(Player player) => _players.TryGetValue(player, out PlayerReputation? reputation) ? reputation : null;

    /// <summary>SMSG_INITIALIZE_FACTIONS for the login and far-teleport packet stage.</summary>
    public byte[] BuildInitializeFactions(Player player) => ReputationPackets.InitializeFactions(For(player));

    public int GetReputation(Player player, uint factionId)
        => For(player) is { } rep && Factions.Find(factionId) is { } faction ? rep.Reputation(faction) : 0;

    public ReputationRank GetRank(Player player, uint factionId) => ReputationMath.ToRank(GetReputation(player, factionId));

    public bool IsAtWar(Player player, uint factionId)
        => For(player) is { } rep && Factions.Find(factionId) is { } faction && rep.State(faction) is { IsAtWar: true };

    byte IPlayerReputation.GetReputationRank(Player player, uint factionId) => (byte)GetRank(player, factionId);

    /// <summary>Player::GetReputationPriceDiscount (vanilla): 10% off at Honored or better. Honor-rank discounts need PvP state.</summary>
    public float GetPriceDiscount(Player player, NpcInfo npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return npc.FactionId != 0 && GetRank(player, npc.FactionId) >= ReputationRank.Honored ? 0.9f : 1f;
    }

    public bool ModifyReputation(Player player, uint factionId, int delta) => Change(player, factionId, delta, incremental: true);

    public bool SetReputation(Player player, uint factionId, int reputation) => Change(player, factionId, reputation, incremental: false);

    public void RewardKill(Player player, Creature victim, float rate = 1f)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(victim);
        if (!_onKill.TryGetValue(victim.Entry, out ReputationOnKillEntry? entry) || For(player) is null)
        {
            return;
        }

        if (entry.Faction1 != 0 && (!entry.TeamDependent || player.Team == Team.Alliance))
        {
            RewardKillFaction(player, victim, entry.Faction1, entry.Value1, entry.MaxStanding1, entry.IsTeamAward1, rate);
        }

        if (entry.Faction2 != 0 && (!entry.TeamDependent || player.Team == Team.Horde))
        {
            RewardKillFaction(player, victim, entry.Faction2, entry.Value2, entry.MaxStanding2, entry.IsTeamAward2, rate);
        }
    }

    public void RewardQuest(Player player, int questLevel, IReadOnlyList<QuestReputationReward> rewards)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rewards);
        uint level = questLevel > 0 ? (uint)questLevel : player.Level;
        foreach (QuestReputationReward reward in rewards)
        {
            if (reward.FactionId == 0 || reward.Value == 0 || Factions.Find(reward.FactionId) is null)
            {
                continue;
            }

            int gain = Gain(ReputationSource.Quest, player, reward.Value, reward.FactionId, level);
            ModifyReputation(player, reward.FactionId, gain);
        }
    }

    public bool TryGetNpcReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, out ReputationRank reaction)
        => ReputationReactions.TryNpcReactionTo(npc, playerTemplate, player, Factions, For(player), out reaction);

    public bool TryGetPlayerReaction(Player player, FactionTemplateRecord npc, FactionTemplateRecord playerTemplate, out ReputationRank reaction)
        => ReputationReactions.TryPlayerReactionTo(npc, playerTemplate, player, Factions, For(player), out reaction);

    /// <summary>CMSG_SET_FACTION_ATWAR after the handler's combat check; persisted, not echoed (the client toggled it).</summary>
    public bool SetAtWar(Player player, int listId, bool atWar)
    {
        // Held by a quest settlement: the settlement writes full standing rows, so a flag change now could
        // be queued behind (and revert) the committed reward. A refused toggle is not echoed to the client.
        if (!player.CanMutateQuestSettlementState || For(player) is not { } rep || !rep.SetAtWarByClient(listId, atWar))
        {
            return false;
        }

        Persist(player, rep);
        return true;
    }

    /// <summary>CMSG_SET_FACTION_INACTIVE; persisted, not echoed.</summary>
    public bool SetInactive(Player player, int listId, bool inactive)
    {
        if (!player.CanMutateQuestSettlementState || For(player) is not { } rep || !rep.SetInactiveByClient(listId, inactive))
        {
            return false;
        }

        Persist(player, rep);
        return true;
    }

    /// <summary>CMSG_SET_WATCHED_FACTION: publishes PLAYER_FIELD_WATCHED_FACTION_INDEX and persists it.</summary>
    public bool SetWatchedFaction(Player player, int listId)
    {
        if (For(player) is not { } rep || !rep.SetWatchedFaction(listId))
        {
            return false;
        }

        player.SetInt32(UpdateFields.PlayerFieldWatchedFactionIndex, listId);
        _sink?.WatchedFactionChanged(player, listId);
        return true;
    }

    /// <summary>Player::CalculateReputationGain with dithering.</summary>
    public int Gain(ReputationSource source, Player player, int rep, uint faction, uint creatureOrQuestLevel)
    {
        ArgumentNullException.ThrowIfNull(player);
        float modifier = rep > 0 ? GainModifier?.Invoke(player, source, faction) ?? 0 : 0;
        float value = ReputationMath.GainBeforeDither(source, rep, player.Level, creatureOrQuestLevel, Rates, modifier);
        return ReputationMath.Dither(value, _roll());
    }

    private void RewardKillFaction(Player player, Creature victim, uint factionId, int value, byte maxStanding, bool teamAward, float rate)
    {
        if (Factions.Find(factionId) is not { } faction)
        {
            return;
        }

        int gain = (int)(Gain(ReputationSource.Kill, player, value, factionId, victim.Level) * rate);
        // Patch 1.9: the cap applies at the end of the MaxStanding rank.
        if ((byte)GetRank(player, factionId) <= maxStanding)
        {
            ModifyReputation(player, factionId, gain);
        }

        if (teamAward && faction.ParentFactionId != 0 && Factions.Find(faction.ParentFactionId) is not null)
        {
            ModifyReputation(player, faction.ParentFactionId, gain / 2);
        }
    }

    /// <summary>
    /// ReputationMgr::SetOneFactionReputation and the client updates, persisted through the sink. Refused
    /// while a quest settlement holds the character (only its own publication step may change standings).
    /// </summary>
    private bool Change(Player player, uint factionId, int value, bool incremental)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.CanMutateQuestSettlementState || !ApplyAndNotify(player, factionId, value, incremental, out PlayerReputation? rep))
        {
            return false;
        }

        Persist(player, rep);
        return true;
    }

    private bool ApplyAndNotify(Player player, uint factionId, int value, bool incremental,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PlayerReputation? rep)
    {
        rep = For(player);
        if (rep is null || Factions.Find(factionId) is not { } faction || rep.State(faction) is not { } state
            || !rep.Apply(faction, value, incremental))
        {
            return false;
        }

        foreach (int listId in rep.TakeNewlyVisible())
        {
            player.Session.Send(WorldOpcode.SmsgSetFactionVisible, ReputationPackets.SetFactionVisible(listId));
        }

        player.Session.Send(WorldOpcode.SmsgSetFactionStanding, ReputationPackets.SetFactionStanding(rep.TakeStandingUpdate(state)));
        return true;
    }

    public bool TryStage(Player player, int questLevel, IReadOnlyList<QuestReputationReward> rewards, out QuestReputationStage stage)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(rewards);
        stage = QuestReputationStage.Empty;
        uint level = questLevel > 0 ? (uint)questLevel : player.Level;
        PlayerReputation? live = For(player);
        PlayerReputation? copy = null;
        var gains = new List<(uint Faction, int Gain)>();
        foreach (QuestReputationReward reward in rewards)
        {
            // The same skip rules as RewardQuest (vmangos RewardReputation ignores a pair with a zero value).
            if (reward.FactionId == 0 || reward.Value == 0 || Factions.Find(reward.FactionId) is not { } faction)
            {
                continue;
            }

            if (live is null)
            {
                return false;
            }

            copy ??= live.Clone();
            int gain = Gain(ReputationSource.Quest, player, reward.Value, reward.FactionId, level);
            if (copy.Apply(faction, gain, incremental: true))
            {
                gains.Add((reward.FactionId, gain));
            }
        }

        if (copy is not null && gains.Count > 0)
        {
            stage = new QuestReputationStage(gains, copy.TakeDirty((int)player.Guid.Low));
        }

        return true;
    }

    public bool Publish(Player player, QuestReputationStage stage)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(stage);
        if (stage.Gains.Count == 0)
        {
            return true;
        }

        PlayerReputation? rep = For(player);
        foreach ((uint faction, int gain) in stage.Gains)
        {
            // The publication scope lifts the settlement hold; no sink call, the transaction persisted the rows.
            ApplyAndNotify(player, faction, gain, incremental: true, out rep);
        }

        if (rep is null)
        {
            return false;
        }

        IReadOnlyList<CharacterReputationRow> live = rep.TakeDirty((int)player.Guid.Low);
        if (live.SequenceEqual(stage.After))
        {
            return true;
        }

        if (live.Count > 0)
        {
            _sink?.FactionsChanged(player, live);
        }

        return false;
    }

    private void Persist(Player player, PlayerReputation rep)
    {
        IReadOnlyList<CharacterReputationRow> rows = rep.TakeDirty((int)player.Guid.Low);
        if (rows.Count > 0)
        {
            _sink?.FactionsChanged(player, rows);
        }
    }
}

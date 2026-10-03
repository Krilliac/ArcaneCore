using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Reputation;

/// <summary>One faction's state for one player (vmangos FactionState). <see cref="Standing"/> is relative to the base.</summary>
public sealed class FactionState(FactionRecord faction, FactionStateFlags flags)
{
    public FactionRecord Faction { get; } = faction;

    public int ListId => Faction.ReputationListId;

    /// <summary>Points relative to the race/class base reputation (what the client and storage carry).</summary>
    public int Standing { get; internal set; }

    public FactionStateFlags Flags { get; internal set; } = flags;

    internal bool NeedSend { get; set; } = true;

    internal bool NeedSave { get; set; }

    public bool IsAtWar => (Flags & FactionStateFlags.AtWar) != 0;
}

/// <summary>
/// Per-player faction standings (vmangos ReputationMgr). Built from Faction.dbc and the
/// player's race/class; stored rows replace the defaults through the same flag rules vmangos
/// applies on load, so a database cannot override forced peace or forced invisibility.
/// World thread once the player is in the world; the session task only builds it.
/// Re-implemented from vmangos/core 4b3d241 ReputationMgr.cpp; no GPL source is copied.
/// </summary>
public sealed class PlayerReputation
{
    private readonly SortedDictionary<int, FactionState> _states = [];
    private readonly List<int> _newlyVisible = [];

    public PlayerReputation(FactionCatalog factions, Race race, Class playerClass)
    {
        ArgumentNullException.ThrowIfNull(factions);
        Factions = factions;
        RaceMask = race == 0 ? 0 : 1u << ((int)race - 1);
        ClassMask = playerClass == 0 ? 0 : 1u << ((int)playerClass - 1);
        foreach (FactionRecord faction in factions.ReputationFactions)
        {
            // ReputationMgr::Initialize: standing 0 relative to the base, the DBC default flags.
            _states[faction.ReputationListId] = new FactionState(faction, (FactionStateFlags)faction.DefaultFlags(RaceMask, ClassMask));
        }
    }

    private PlayerReputation(PlayerReputation source)
    {
        Factions = source.Factions;
        RaceMask = source.RaceMask;
        ClassMask = source.ClassMask;
        WatchedFaction = source.WatchedFaction;
        PeaceForcedUsesEffectiveStanding = source.PeaceForcedUsesEffectiveStanding;
        foreach ((int listId, FactionState state) in source._states)
        {
            _states[listId] = new FactionState(state.Faction, state.Flags)
            {
                Standing = state.Standing, NeedSend = state.NeedSend, NeedSave = state.NeedSave,
            };
        }

        _newlyVisible.AddRange(source._newlyVisible);
    }

    public FactionCatalog Factions { get; }

    public uint RaceMask { get; }

    public uint ClassMask { get; }

    /// <summary>PLAYER_FIELD_WATCHED_FACTION_INDEX: a reputation-list slot, -1 for none.</summary>
    public int WatchedFaction { get; internal set; } = -1;

    /// <summary>
    /// Option <c>Reputation:PeaceForcedUsesEffectiveStanding</c>. Retail (default false) compares the RELATIVE standing in the
    /// forced-peace exception of SetAtWar (ReputationMgr.cpp:334-336); true compares the effective rank (base included).
    /// </summary>
    public bool PeaceForcedUsesEffectiveStanding { get; init; }

    public IEnumerable<FactionState> States => _states.Values;

    public FactionState? State(FactionRecord faction)
        => faction.CanHaveReputation ? _states.GetValueOrDefault(faction.ReputationListId) : null;

    public FactionState? StateByListId(int listId) => _states.GetValueOrDefault(listId);

    public int BaseReputation(FactionRecord faction) => faction.BaseReputation(RaceMask, ClassMask);

    /// <summary>ReputationMgr::GetReputation: base + standing, or 0 without a state.</summary>
    public int Reputation(FactionRecord faction) => State(faction) is { } state ? BaseReputation(faction) + state.Standing : 0;

    public ReputationRank Rank(FactionRecord faction) => ReputationMath.ToRank(Reputation(faction));

    /// <summary>ReputationMgr::LoadFromDB. Rows for unknown or reputation-less factions are ignored.</summary>
    public void Load(IEnumerable<CharacterReputationRow> rows, int watchedFaction)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (CharacterReputationRow row in rows)
        {
            if (Factions.Find(row.Faction) is not { CanHaveReputation: true } faction || State(faction) is not { } state)
            {
                continue;
            }

            int baseRep = BaseReputation(faction);
            state.Standing = ReputationMath.Clamp((long)baseRep + row.Standing) - baseRep;
            var stored = (FactionStateFlags)row.Flags;
            if ((stored & FactionStateFlags.Visible) != 0)
            {
                MakeVisible(state, announce: false);
            }

            if ((stored & FactionStateFlags.Inactive) != 0)
            {
                SetInactive(state, true);
            }

            if ((stored & FactionStateFlags.AtWar) != 0)
            {
                SetAtWar(state, true);
            }
            else if ((state.Flags & FactionStateFlags.Visible) != 0)
            {
                SetAtWar(state, false);
            }

            if (Rank(faction) <= ReputationRank.Hostile)
            {
                SetAtWar(state, true);
            }

            // Matching stored flags need no resend or save; standing alone came from storage.
            if (state.Flags == stored)
            {
                state.NeedSend = false;
                state.NeedSave = false;
            }
        }

        WatchedFaction = watchedFaction >= -1 && (watchedFaction == -1 || _states.ContainsKey(watchedFaction)) ? watchedFaction : -1;
        _newlyVisible.Clear();
    }

    /// <summary>
    /// ReputationMgr::SetOneFactionReputation: set (or add, when <paramref name="incremental"/>)
    /// reputation, clamped to Hated..Exalted; makes the faction visible and declares war at Hostile.
    /// False when the faction has no state.
    /// </summary>
    public bool Apply(FactionRecord faction, int value, bool incremental)
    {
        ArgumentNullException.ThrowIfNull(faction);
        if (State(faction) is not { } state)
        {
            return false;
        }

        int baseRep = BaseReputation(faction);
        long total = incremental ? (long)value + state.Standing + baseRep : value;
        int clamped = ReputationMath.Clamp(total);
        state.Standing = clamped - baseRep;
        state.NeedSend = true;
        state.NeedSave = true;
        MakeVisible(state, announce: true);
        if (ReputationMath.ToRank(clamped) <= ReputationRank.Hostile)
        {
            SetAtWar(state, true);
        }

        return true;
    }

    /// <summary>
    /// CMSG_SET_FACTION_ATWAR (ReputationMgr::SetAtWar(repListID)): unknown, hidden or forced-invisible
    /// factions cannot change; forced peace blocks war unless the faction is already Hated.
    /// </summary>
    public bool SetAtWarByClient(int listId, bool atWar)
        => _states.TryGetValue(listId, out FactionState? state)
            && (state.Flags & (FactionStateFlags.InvisibleForced | FactionStateFlags.Hidden)) == 0
            && SetAtWar(state, atWar);

    /// <summary>CMSG_SET_FACTION_INACTIVE: only a visible, not hidden/forced-invisible faction may become inactive.</summary>
    public bool SetInactiveByClient(int listId, bool inactive)
        => _states.TryGetValue(listId, out FactionState? state) && SetInactive(state, inactive);

    /// <summary>CMSG_SET_WATCHED_FACTION: -1 clears; otherwise the slot must exist and be visible.</summary>
    public bool SetWatchedFaction(int listId)
    {
        if (listId != -1 && (!_states.TryGetValue(listId, out FactionState? state) || (state.Flags & FactionStateFlags.Visible) == 0))
        {
            return false;
        }

        if (WatchedFaction == listId)
        {
            return false;
        }

        WatchedFaction = listId;
        return true;
    }

    /// <summary>
    /// A deep copy (states, flags, pending-send and pending-save marks, watched slot, pending visibility
    /// announcements) for staging a change without touching the live standings.
    /// </summary>
    internal PlayerReputation Clone() => new(this);

    /// <summary>List slots made visible since the last call (SMSG_SET_FACTION_VISIBLE once each).</summary>
    public IReadOnlyList<int> TakeNewlyVisible()
    {
        int[] taken = [.. _newlyVisible];
        _newlyVisible.Clear();
        return taken;
    }

    /// <summary>
    /// ReputationMgr::SendState: the changed faction first, then every other faction waiting to be
    /// sent; clears their pending-send marks.
    /// </summary>
    public IReadOnlyList<(int ListId, int Standing)> TakeStandingUpdate(FactionState primary)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var entries = new List<(int, int)> { (primary.ListId, primary.Standing) };
        foreach (FactionState state in _states.Values)
        {
            if (state.NeedSend)
            {
                state.NeedSend = false;
                if (state.ListId != primary.ListId)
                {
                    entries.Add((state.ListId, state.Standing));
                }
            }
        }

        return entries;
    }

    /// <summary>The initial packet clears every pending send (SendInitialReputations).</summary>
    public void MarkAllSent()
    {
        foreach (FactionState state in _states.Values)
        {
            state.NeedSend = false;
        }
    }

    /// <summary>Rows that changed since the last call (ReputationMgr::SaveToDB); clears their save marks.</summary>
    public IReadOnlyList<CharacterReputationRow> TakeDirty(int characterId)
    {
        var rows = new List<CharacterReputationRow>();
        foreach (FactionState state in _states.Values)
        {
            if (state.NeedSave)
            {
                state.NeedSave = false;
                rows.Add(new CharacterReputationRow(characterId, state.Faction.Id, state.Standing, (uint)state.Flags));
            }
        }

        return rows;
    }

    private void MakeVisible(FactionState state, bool announce)
    {
        if ((state.Flags & (FactionStateFlags.InvisibleForced | FactionStateFlags.Hidden | FactionStateFlags.Visible)) != 0)
        {
            return;
        }

        state.Flags |= FactionStateFlags.Visible;
        state.NeedSend = true;
        state.NeedSave = true;
        if (announce)
        {
            _newlyVisible.Add(state.ListId);
        }
    }

    private bool SetAtWar(FactionState state, bool atWar)
    {
        if (state.IsAtWar == atWar)
        {
            return false;
        }

        // ReputationMgr.cpp:334-336: ReputationToRank(faction->Standing) is the RELATIVE standing. The effective
        // rank is only used behind Reputation:PeaceForcedUsesEffectiveStanding.
        ReputationRank peaceRank = PeaceForcedUsesEffectiveStanding ? Rank(state.Faction) : ReputationMath.ToRank(state.Standing);
        if (atWar && (state.Flags & FactionStateFlags.PeaceForced) != 0 && peaceRank > ReputationRank.Hated)
        {
            return false;
        }

        state.Flags = atWar ? state.Flags | FactionStateFlags.AtWar : state.Flags & ~FactionStateFlags.AtWar;
        state.NeedSend = true;
        state.NeedSave = true;
        return true;
    }

    private static bool SetInactive(FactionState state, bool inactive)
    {
        if (inactive && ((state.Flags & (FactionStateFlags.InvisibleForced | FactionStateFlags.Hidden)) != 0
            || (state.Flags & FactionStateFlags.Visible) == 0))
        {
            return false;
        }

        if (((state.Flags & FactionStateFlags.Inactive) != 0) == inactive)
        {
            return false;
        }

        state.Flags = inactive ? state.Flags | FactionStateFlags.Inactive : state.Flags & ~FactionStateFlags.Inactive;
        state.NeedSend = true;
        state.NeedSave = true;
        return true;
    }
}

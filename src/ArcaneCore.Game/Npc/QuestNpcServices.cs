using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Npc;

/// <summary>Collaborators owned by other areas; any of them may be missing (the dependent features then fail closed).</summary>
public sealed record QuestNpcDependencies(
    ICreatureLookup? Creatures = null,
    IItemService? Items = null,
    ISpellLearner? Spells = null,
    IPlayerExperience? Experience = null,
    IPlayerReputation? Reputation = null,
    IConditionEvaluator? Conditions = null,
    ITaxiFlights? Flights = null,
    IMapInfo? Maps = null,
    IResurrection? Resurrection = null,
    Progression.IQuestRewardEffects? RewardEffects = null,
    IQuestReputationSettlement? ReputationRewards = null);

/// <summary>Where quest/NPC state changes go (the world daemon's save queue). World thread.</summary>
public interface IQuestNpcSink
{
    /// <summary>Quest rows changed (delta).</summary>
    void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows);

    /// <summary>The known flight-path mask changed.</summary>
    void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask);

    /// <summary>Money or the bind point changed: save the character row.</summary>
    void CharacterChanged(Player player);
}

/// <summary>Per-player quest/NPC state (vmangos keeps it on Player; here the feature owns it).</summary>
public sealed class PlayerNpcState(Player player)
{
    public PlayerQuestLog Quests { get; } = new(player);

    /// <summary>vmangos PlayerTalkClass.</summary>
    public PlayerMenu Menu { get; } = new();

    /// <summary>vmangos PlayerTaxi::m_taximask.</summary>
    public uint[] TaxiMask { get; } = new uint[NpcStore.TaxiMaskSize];

    /// <summary>Quest statuses and taxi mask have been loaded; until then requests are ignored.</summary>
    public bool Loaded { get; set; }
}

/// <summary>
/// Quests, gossip, vendors, trainers, innkeepers and flight masters for every online player
/// (vmangos Player quest system, PlayerMenu, and the NPC/Quest/Taxi handlers). World thread only;
/// content stores are immutable.
/// </summary>
public sealed partial class QuestNpcServices : IQuestObjectiveEvents
{
    /// <summary>INTERACTION_DISTANCE (vmangos ObjectDefines.h).</summary>
    public const float InteractionDistance = 5.0f;

    /// <summary>vmangos DEFAULT_GOSSIP_MESSAGE.</summary>
    public const uint DefaultGossipMessage = 0xFFFFFF;

    /// <summary>MAX_MONEY_AMOUNT (vmangos Player.h: 0x7FFFFFFF - 1).</summary>
    public const uint MaxMoneyAmount = 0x7FFFFFFE;

    private readonly Dictionary<ObjectGuid, PlayerNpcState> _players = [];
    private readonly IQuestNpcSink _sink;
    private readonly Func<long> _unixNow;
    private readonly ILogger _logger;

    public QuestNpcServices(
        QuestStore quests,
        NpcStore npcs,
        QuestNpcDependencies dependencies,
        QuestNpcOptions options,
        IQuestNpcSink sink,
        Func<long> unixNow,
        ILogger logger)
    {
        _quests = quests;
        _npcs = npcs;
        Deps = dependencies;
        Options = options;
        _sink = sink;
        _unixNow = unixNow;
        _logger = logger;
    }

    // The stores are immutable; the live reload (.reload quest_template, npc_vendor, ...) swaps whole
    // stores on the world thread, and the session tasks that read them see the old one or the new one.
    private QuestStore _quests;
    private NpcStore _npcs;

    public QuestStore Quests => Volatile.Read(ref _quests);

    public NpcStore Npcs => Volatile.Read(ref _npcs);

    /// <summary>Swap the quest content (live reload, world thread); returns the store it replaced.</summary>
    public QuestStore ReplaceQuests(QuestStore quests) => Interlocked.Exchange(ref _quests, quests ?? throw new ArgumentNullException(nameof(quests)));

    /// <summary>Swap the NPC service content (live reload, world thread); returns the store it replaced.</summary>
    public NpcStore ReplaceNpcs(NpcStore npcs) => Interlocked.Exchange(ref _npcs, npcs ?? throw new ArgumentNullException(nameof(npcs)));

    public QuestNpcDependencies Deps { get; }

    public QuestNpcOptions Options { get; }

    // ---- player lifecycle --------------------------------------------------------------

    /// <summary>A player entered the world: its state exists but is not loaded yet.</summary>
    public PlayerNpcState Track(Player player)
    {
        var state = new PlayerNpcState(player);
        _players[player.Guid] = state;
        return state;
    }

    /// <summary>The player left the world.</summary>
    public void Untrack(Player player)
    {
        if (StateOf(player) is { } state && ReferenceEquals(state.Quests.Player, player))
        {
            _players.Remove(player.Guid);
        }
    }

    public PlayerNpcState? StateOf(Player player) => _players.GetValueOrDefault(player.Guid);

    /// <summary>
    /// Apply the loaded rows (vmangos _LoadQuestStatus, PlayerTaxi::LoadTaxiMask — known bits
    /// are masked with the existing nodes). A character without a stored mask gets the race's
    /// starting mask (vmangos PlayerTaxi::InitTaxiNodes, at character creation there).
    /// </summary>
    /// <remarks>
    /// <paramref name="state"/> is the instance <see cref="Track"/> returned: a load finishing
    /// after the player relogged (a newer state) is dropped, so stale rows are never applied.
    /// </remarks>
    public void CompleteLoad(PlayerNpcState state, CharacterQuestData data)
    {
        Player player = state.Quests.Player;
        if (state.Loaded || !ReferenceEquals(StateOf(player), state))
        {
            return;
        }

        state.Quests.Load(data.Quests, Quests);
        if (data.TaxiMask.Count > 0)
        {
            for (int i = 0; i < NpcStore.TaxiMaskSize; i++)
            {
                state.TaxiMask[i] = i < data.TaxiMask.Count ? data.TaxiMask[i] & Npcs.TaxiNodesMask[i] : 0;
            }
        }
        else if (Npcs.RaceStartingTaxiMask((byte)player.Race) is var start and not 0)
        {
            state.TaxiMask[0] = start;
            _sink.TaxiMaskChanged(player, [.. state.TaxiMask]);
        }

        state.Loaded = true;
        CheckTimers(player);
    }

    // ---- shared helpers -----------------------------------------------------------------

    /// <summary>The loaded state of an online player, or null (requests are then ignored).</summary>
    private PlayerNpcState? Ready(Player player)
        => player.CanMutateQuestSettlementState && _players.TryGetValue(player.Guid, out PlayerNpcState? s)
            && s.Loaded && ReferenceEquals(s.Quests.Player, player) ? s : null;

    /// <summary>vmangos Player::GetClassMask/GetRaceMask: the bit for a one-based id.</summary>
    private static uint Mask(byte id) => id is > 0 and <= 32 ? 1u << (id - 1) : 0;

    /// <summary>Hand pending quest row changes to the sink.</summary>
    private void Flush(PlayerNpcState state)
    {
        if (state.Quests.HasChanges)
        {
            _sink.QuestsChanged(state.Quests.Player, state.Quests.TakeChanges((int)state.Quests.Player.Guid.Low));
        }
    }

    private long UnixNow => _unixNow();

    private uint DisplayOf(uint itemId) => itemId != 0 ? Deps.Items?.GetItem(itemId)?.DisplayId ?? 0 : 0;

    private static void Send(Player player, WorldOpcode opcode, PacketWriter body) => player.Session.Send(opcode, body.AsSpan());

    /// <summary>vmangos Player::ModifyMoney (clamped to 0..MAX_MONEY_AMOUNT), then MoneyChanged.</summary>
    private void ModifyMoney(PlayerNpcState state, long delta, bool saveCharacter = true)
    {
        Player player = state.Quests.Player;
        long money = Math.Clamp((long)player.Money + delta, 0, MaxMoneyAmount);
        player.Money = (uint)money;
        MoneyChanged(state, (uint)money);
        if (saveCharacter)
        {
            _sink.CharacterChanged(player);
        }
    }

    /// <summary>
    /// vmangos Player::GetNPCIfCanInteractWith / CanInteractWithNPC: in the world and not on a
    /// flight, the creature exists, carries <paramref name="flags"/>, is alive, not hostile, not in
    /// combat, selectable, and within INTERACTION_DISTANCE (3D, measured between bounding radii).
    /// </summary>
    /// <remarks>
    /// Reputation eligibility is separate from hostility: an Unfriendly NPC is not hostile,
    /// but refuses interaction. Ghost visibility remains the creature lookup owner's check.
    /// </remarks>
    public NpcInfo? InteractableNpc(Player player, ObjectGuid guid, NpcFlags flags)
    {
        // Player.cpp CanInteractWithNPC rejects lost control / inability to react.
        const UnitFlags unavailable = UnitFlags.TaxiFlight | UnitFlags.Stunned | UnitFlags.Confused | UnitFlags.Fleeing;
        if (guid.IsEmpty || !player.IsInWorld || (player.UnitFlags & unavailable) != 0)
        {
            return null;
        }

        NpcInfo? npc = Deps.Creatures?.Find(player, guid);
        if (npc is null || npc.MapId != player.MapId)
        {
            return null;
        }

        // Spirit healers and guides are invisible to the living and the only NPCs the dead can use
        // (vmangos IsInvisibleForAlive / m_isSpiritService; ghosts only see spirit services).
        bool spiritService = (npc.NpcFlags & (NpcFlags.SpiritHealer | NpcFlags.SpiritGuide)) != 0;
        if (player.IsAlive == spiritService)
        {
            return null;
        }

        if (flags != NpcFlags.None && (npc.NpcFlags & flags) == 0)
        {
            return null;
        }

        if (flags == NpcFlags.StableMaster && player.Class != Class.Hunter)
        {
            return null;
        }

        if (!npc.IsAlive || npc.IsHostile || npc.IsInCombat || npc.IsNotSelectable)
        {
            return null;
        }

        // vmangos 4b3d241 Player::CanInteractWithNPC also rejects ranks <= Unfriendly.
        // The reputation owner returns Neutral for factions without a reputation list.
        if (npc.FactionId != 0 && Deps.Reputation is { } reputation
            && reputation.GetReputationRank(player, npc.FactionId) <= (byte)ReputationRank.Unfriendly)
        {
            return null;
        }

        float dx = npc.X - player.X;
        float dy = npc.Y - player.Y;
        float dz = npc.Z - player.Z;
        float distSq = (dx * dx) + (dy * dy) + (dz * dz);
        if (npc.IsGameObject)
        {
            // vmangos GameObject::IsAtInteractDistance (GameObject.cpp:2584-2609), no-bounds branch:
            // GetDistance3dToCenter <= GetInteractionDistance() (GameObjectDefines.h:759-785; 5.55556 for quest givers).
            // The display-bounds oriented box branch is a documented limit (see GameObjectMapSystem.InteractionDistanceFor).
            float reach = npc.GameObjectInteractionDistance;
            return float.IsFinite(reach) && reach >= 0 && distSq <= reach * reach ? npc : null;
        }

        // Object.cpp IsWithinDist: strict 3D radius-adjusted comparison.
        float range = InteractionDistance + npc.BoundingRadius + player.BoundingRadius;
        return float.IsFinite(range) && range > 0 && distSq < range * range ? npc : null;
    }

    /// <summary>A creature the player can see (vmangos GetObjectByTypeMask(TYPEMASK_CREATURE…)), without interaction checks.</summary>
    private NpcInfo? FindNpc(Player player, ObjectGuid guid)
        => guid.IsEmpty || Deps.Creatures?.Find(player, guid) is not { } npc || npc.MapId != player.MapId ? null : npc;

    /// <summary>vmangos PlayerMenu::CloseGossip → SMSG_GOSSIP_COMPLETE (empty).</summary>
    private static void CloseGossip(Player player) => player.Session.Send(WorldOpcode.SmsgGossipComplete, []);

    private void LogMissing(string what, ObjectGuid npc) => _logger.LogDebug("{What}: {Npc} not found or not interactable", what, npc);
}

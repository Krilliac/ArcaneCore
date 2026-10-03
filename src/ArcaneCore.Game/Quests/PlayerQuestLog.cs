using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Quests;

/// <summary>One quest's progress (vmangos QuestStatusData).</summary>
public sealed class QuestStatusData
{
    public QuestStatus Status { get; set; }

    public bool Rewarded { get; set; }

    public bool Explored { get; set; }

    /// <summary>Unix time (s) at which a running timed quest fails; 0 when untimed or failed.</summary>
    public long TimerEndUnix { get; set; }

    public uint RewardChoice { get; set; }

    public uint[] CreatureOrGOCount { get; } = new uint[QuestConstants.ObjectivesCount];

    public uint[] ItemCount { get; } = new uint[QuestConstants.ObjectivesCount];
}

/// <summary>
/// A player's quest statuses and quest log slots. The log lives in the PLAYER_QUEST_LOG_x_y
/// update fields exactly as vmangos keeps it (Player.h SetQuestSlot*): per slot the quest id,
/// a count/state word (6-bit counters at bit 6·i, state flags in byte 3) and the timer.
/// Changes are tracked per quest for delta persistence. World thread only.
/// </summary>
public sealed class PlayerQuestLog(Player player)
{
    private readonly Dictionary<uint, QuestStatusData> _statuses = [];
    private readonly HashSet<uint> _changed = [];
    private readonly HashSet<uint> _timed = [];

    public Player Player { get; } = player;

    /// <summary>The quest ids of running timed quests (vmangos m_timedquests).</summary>
    public IReadOnlyCollection<uint> TimedQuests => _timed;

    public IReadOnlyDictionary<uint, QuestStatusData> Statuses => _statuses;

    /// <summary>
    /// Fill the log from the characters database (vmangos Player::_LoadQuestStatus): statuses of
    /// known quests are kept; running, complete or failed quests that are not rewarded (or are
    /// repeatable) take log slots in load order, with their state flags and kill/cast counters.
    /// </summary>
    public void Load(IEnumerable<CharacterQuestStatus> rows, QuestStore store)
    {
        _statuses.Clear();
        _changed.Clear();
        _timed.Clear();
        int slot = 0;
        foreach (CharacterQuestStatus row in rows)
        {
            Quest? quest = store.Get(row.Quest);
            if (quest is null)
            {
                continue;
            }

            var data = new QuestStatusData
            {
                Status = row.Status <= (byte)QuestStatus.Failed ? (QuestStatus)row.Status : QuestStatus.None,
                Rewarded = row.Rewarded,
                Explored = row.Explored,
                RewardChoice = row.RewardChoice,
            };
            data.CreatureOrGOCount[0] = row.MobCount1;
            data.CreatureOrGOCount[1] = row.MobCount2;
            data.CreatureOrGOCount[2] = row.MobCount3;
            data.CreatureOrGOCount[3] = row.MobCount4;
            data.ItemCount[0] = row.ItemCount1;
            data.ItemCount[1] = row.ItemCount2;
            data.ItemCount[2] = row.ItemCount3;
            data.ItemCount[3] = row.ItemCount4;
            _statuses[row.Quest] = data;

            // Timed and running: keep the end time (an expired one fails at the next timer check).
            uint slotTimer = 0;
            if (quest.HasSpecialFlag(QuestSpecialFlags.Timed) && !RewardStatus(quest) && data.Status is QuestStatus.Incomplete or QuestStatus.Complete
                && row.Timer != 0)
            {
                _timed.Add(row.Quest);
                data.TimerEndUnix = row.Timer;
                slotTimer = (uint)Math.Clamp(row.Timer, 0, uint.MaxValue);
            }

            if (slot < QuestConstants.MaxQuestLogSize
                && data.Status is QuestStatus.Incomplete or QuestStatus.Complete or QuestStatus.Failed
                && (!data.Rewarded || quest.IsRepeatable))
            {
                SetSlot(slot, row.Quest, slotTimer);
                if (data.Explored || data.Status == QuestStatus.Complete)
                {
                    SetSlotState(slot, QuestConstants.SlotStateComplete);
                }

                if (data.Status == QuestStatus.Failed)
                {
                    SetSlotState(slot, QuestConstants.SlotStateFail);
                }

                for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
                {
                    if (data.CreatureOrGOCount[i] != 0)
                    {
                        SetSlotCounter(slot, i, data.CreatureOrGOCount[i]);
                    }
                }

                slot++;
            }
        }

        for (int i = slot; i < QuestConstants.MaxQuestLogSize; i++)
        {
            if (SlotQuestId(i) != 0)
            {
                SetSlot(i, 0);
            }
        }
    }

    public QuestStatusData? Get(uint questId) => _statuses.GetValueOrDefault(questId);

    /// <summary>vmangos mQuestStatus[questId] (find or create).</summary>
    public QuestStatusData GetOrAdd(uint questId)
    {
        if (!_statuses.TryGetValue(questId, out QuestStatusData? data))
        {
            _statuses[questId] = data = new QuestStatusData();
        }

        return data;
    }

    /// <summary>vmangos Player::GetQuestStatus.</summary>
    public QuestStatus GetStatus(uint questId) => questId != 0 && _statuses.TryGetValue(questId, out QuestStatusData? d) ? d.Status : QuestStatus.None;

    /// <summary>vmangos Player::GetQuestRewardStatus: rewarded, and neither status NONE nor repeatable.</summary>
    public bool RewardStatus(Quest quest)
        => _statuses.TryGetValue(quest.Id, out QuestStatusData? d) && d.Status != QuestStatus.None && !quest.IsRepeatable && d.Rewarded;

    /// <summary>vmangos Player::IsCurrentQuest(questId, completedOrNot = 0).</summary>
    public bool IsCurrent(uint questId)
        => _statuses.TryGetValue(questId, out QuestStatusData? d)
            && (d.Status == QuestStatus.Incomplete || (d.Status == QuestStatus.Complete && !d.Rewarded));

    /// <summary>Mark a quest's row as needing an upsert (vmangos uState = QUEST_CHANGED/NEW).</summary>
    public void MarkChanged(uint questId) => _changed.Add(questId);

    public void AddTimed(uint questId) => _timed.Add(questId);

    public void RemoveTimed(uint questId) => _timed.Remove(questId);

    /// <summary>Take the pending upserts (rows for the save queue).</summary>
    public IReadOnlyList<CharacterQuestStatus> TakeChanges(int characterId)
    {
        var upserts = new List<CharacterQuestStatus>(_changed.Count);
        foreach (uint questId in _changed.Order())
        {
            if (_statuses.TryGetValue(questId, out QuestStatusData? d))
            {
                upserts.Add(new CharacterQuestStatus(characterId, questId, (byte)d.Status, d.Rewarded, d.Explored, d.TimerEndUnix,
                    d.CreatureOrGOCount[0], d.CreatureOrGOCount[1], d.CreatureOrGOCount[2], d.CreatureOrGOCount[3],
                    d.ItemCount[0], d.ItemCount[1], d.ItemCount[2], d.ItemCount[3], d.RewardChoice));
            }
        }

        _changed.Clear();
        return upserts;
    }

    public bool HasChanges => _changed.Count > 0;

    // ---- quest log slots (vmangos Player.h) ----------------------------------------------

    /// <summary>vmangos GetQuestSlotQuestId.</summary>
    public uint SlotQuestId(int slot) => Player.GetUInt32(Field(slot, 0));

    /// <summary>vmangos FindQuestSlot: the slot holding <paramref name="questId"/> (0 = first free), or MAX_QUEST_LOG_SIZE.</summary>
    public int FindSlot(uint questId)
    {
        for (int i = 0; i < QuestConstants.MaxQuestLogSize; i++)
        {
            if (SlotQuestId(i) == questId)
            {
                return i;
            }
        }

        return QuestConstants.MaxQuestLogSize;
    }

    /// <summary>vmangos SetQuestSlot: id, cleared count/state word, timer.</summary>
    public void SetSlot(int slot, uint questId, uint timer = 0)
    {
        Player.SetUInt32(Field(slot, 0), questId);
        Player.SetUInt32(Field(slot, 1), 0);
        Player.SetUInt32(Field(slot, 2), timer);
    }

    /// <summary>vmangos SetQuestSlotCounter: a 6-bit counter at bit 6·counter.</summary>
    public void SetSlotCounter(int slot, int counter, uint count)
    {
        uint value = Player.GetUInt32(Field(slot, 1));
        value &= ~(0x3Fu << (counter * 6));
        value |= (count & 0x3Fu) << (counter * 6);
        Player.SetUInt32(Field(slot, 1), value);
    }

    /// <summary>vmangos SetQuestSlotState: SetByteFlag(count/state word, byte 3, state).</summary>
    public void SetSlotState(int slot, byte state)
        => Player.SetByte(Field(slot, 1), 3, (byte)(Player.GetByte(Field(slot, 1), 3) | state));

    /// <summary>vmangos RemoveQuestSlotState.</summary>
    public void RemoveSlotState(int slot, byte state)
        => Player.SetByte(Field(slot, 1), 3, (byte)(Player.GetByte(Field(slot, 1), 3) & ~state));

    /// <summary>vmangos SetQuestSlotTimer.</summary>
    public void SetSlotTimer(int slot, uint timer) => Player.SetUInt32(Field(slot, 2), timer);

    /// <summary>vmangos Player::SwapQuestSlot: exchange the three fields of two slots.</summary>
    public void SwapSlots(int slot1, int slot2)
    {
        for (int i = 0; i < QuestConstants.FieldsPerSlot; i++)
        {
            uint a = Player.GetUInt32(Field(slot1, i));
            uint b = Player.GetUInt32(Field(slot2, i));
            Player.SetUInt32(Field(slot1, i), b);
            Player.SetUInt32(Field(slot2, i), a);
        }
    }

    private static int Field(int slot, int offset) => UpdateFields.PlayerQuestLog11 + (slot * QuestConstants.FieldsPerSlot) + offset;
}

using System.Text;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

public sealed record GroupListMember(string Name, ulong Guid, byte Status, byte Flags);

/// <summary>SMSG_GROUP_LIST (GroupPackets.BuildGroupList / BuildEmptyGroupList).</summary>
public sealed record GroupListView(GroupType Type, byte OwnFlags, IReadOnlyList<GroupListMember> Members, ulong Leader,
    LootMethod? LootMethod, ulong MasterLooter, byte LootThreshold)
{
    public bool IsEmpty => Members.Count == 0 && Leader == 0;
}

/// <summary>SMSG_PARTY_COMMAND_RESULT: u32 operation, CString name, u32 result.</summary>
public sealed record PartyCommandResultView(PartyOperation Operation, string Name, PartyResult Result);

/// <summary>SMSG_TRADE_STATUS (EconomyPackets.TradeStatus): the GUID is present for BeginTrade, the inventory result for CloseWindow.</summary>
public sealed record TradeStatusView(TradeStatus Status, ulong Guid, uint InventoryResult);

/// <summary>SMSG_SEND_MAIL_RESULT (EconomyPackets.SendMailResult).</summary>
public sealed record MailResultView(uint MailId, MailAction Action, MailResult Result, uint EquipError, uint ItemGuid, uint ItemCount);

/// <summary>SMSG_DUEL_REQUESTED: u64 arbiter (the flag), u64 initiator.</summary>
public sealed record DuelRequestedView(ulong Arbiter, ulong Initiator);

/// <summary>SMSG_DUEL_WINNER: u8 reason (0 won, 1 fled), CString winner, CString loser.</summary>
public sealed record DuelWinnerView(byte Reason, string Winner, string Loser);

public sealed record LootItemView(byte Slot, uint ItemId, uint Count, uint DisplayId, byte SlotType);

/// <summary>SMSG_LOOT_RESPONSE (LootPackets.LootResponse).</summary>
public sealed record LootResponseView(ulong Source, byte LootType, uint Gold, IReadOnlyList<LootItemView> Items);

/// <summary>SMSG_ATTACKERSTATEUPDATE (CombatPackets.AttackerStateUpdate).</summary>
public sealed record AttackerStateView(HitInfo HitInfo, ulong Attacker, ulong Victim, uint TotalDamage, VictimState VictimState,
    uint MeleeSpellId, uint Blocked);

/// <summary>SMSG_SPELL_GO (SpellPackets.BuildSpellGo) up to the hit and miss lists.</summary>
public sealed record SpellGoView(ulong CastItemOrCaster, ulong Caster, uint SpellId, ushort Flags, IReadOnlyList<ulong> Hits,
    IReadOnlyList<(ulong Guid, byte Reason)> Misses);

/// <summary>SMSG_CAST_RESULT (SpellPackets.BuildCastResult).</summary>
public sealed record CastResultView(uint SpellId, bool Success, SpellCastResult Reason);

/// <summary>SMSG_SPELL_FAILURE: u64 caster, u32 spell, u8 reason.</summary>
public sealed record SpellFailureView(ulong Caster, uint SpellId, SpellCastResult Reason);

/// <summary>SMSG_MESSAGECHAT of a player or system message (ChatPackets.BuildMessage; not channel or monster chat).</summary>
public sealed record ChatMessageView(ChatType Type, Language Language, ulong Sender, string Message, ChatTag Tag);

/// <summary>SMSG_LOG_XPGAIN (ProgressionPackets.LogXpGain): victim (0 for non-kill XP), total XP, 0 kill / 1 other, kill XP.</summary>
public sealed record XpGainView(ulong Victim, uint TotalXp, bool Kill, uint KillXp);

/// <summary>SMSG_QUESTUPDATE_ADD_KILL: quest, entry (high bit: game object), count, required, u64 GUID.</summary>
public sealed record QuestKillView(uint Quest, uint Entry, uint Count, uint Required, ulong Guid);

/// <summary>SMSG_QUESTGIVER_QUEST_COMPLETE (QuestPackets.Complete) up to the reward item count.</summary>
public sealed record QuestCompleteView(uint Quest, uint Experience, uint Money, uint RewardItems);

/// <summary>
/// Typed decoders for the server (SMSG) packets the scenario steps wait for. Each mirrors the server's own writer named in
/// its record's summary and throws <see cref="FormatException"/> on a short or malformed body.
/// </summary>
public static class ScenarioDecoders
{
    public static GroupListView GroupList(byte[] payload) => Decode(payload, nameof(GroupList), static data =>
    {
        var r = new PacketReader(data);
        var type = (GroupType)r.ReadByte();
        byte own = r.ReadByte();
        uint count = r.ReadUInt32();
        if (count > Group.MaxRaidSize) throw new FormatException("group list member count");
        var members = new List<GroupListMember>((int)count);
        for (uint i = 0; i < count; i++)
        {
            string name = r.ReadCString();
            ulong guid = r.ReadUInt64();
            byte status = r.ReadByte();
            members.Add(new GroupListMember(name, guid, status, r.ReadByte()));
        }

        ulong leader = r.ReadUInt64();
        if (r.Remaining == 0) return new GroupListView(type, own, members, leader, null, 0, 0);
        var method = (LootMethod)r.ReadByte();
        ulong master = r.ReadUInt64();
        return new GroupListView(type, own, members, leader, method, master, r.ReadByte());
    });

    public static PartyCommandResultView PartyCommandResult(byte[] payload) => Decode(payload, nameof(PartyCommandResult), static data =>
    {
        var r = new PacketReader(data);
        var operation = (PartyOperation)r.ReadUInt32();
        string name = r.ReadCString();
        return new PartyCommandResultView(operation, name, (PartyResult)r.ReadUInt32());
    });

    /// <summary>SMSG_GROUP_INVITE: the inviter's name.</summary>
    public static string GroupInvite(byte[] payload) => Decode(payload, nameof(GroupInvite), static data => new PacketReader(data).ReadCString());

    public static TradeStatusView TradeStatus(byte[] payload) => Decode(payload, nameof(TradeStatus), static data =>
    {
        var r = new PacketReader(data);
        var status = (Game.Economy.TradeStatus)r.ReadUInt32();
        return status switch
        {
            Game.Economy.TradeStatus.BeginTrade => new TradeStatusView(status, r.ReadUInt64(), 0),
            Game.Economy.TradeStatus.CloseWindow => new TradeStatusView(status, 0, r.ReadUInt32()),
            _ => new TradeStatusView(status, 0, 0),
        };
    });

    public static MailResultView MailResult(byte[] payload) => Decode(payload, nameof(MailResult), static data =>
    {
        var r = new PacketReader(data);
        uint id = r.ReadUInt32();
        var action = (MailAction)r.ReadUInt32();
        var result = (Game.Economy.MailResult)r.ReadUInt32();
        if (result == Game.Economy.MailResult.EquipError) return new MailResultView(id, action, result, r.ReadUInt32(), 0, 0);
        if (action == MailAction.ItemTaken && r.Remaining >= 8)
        {
            uint item = r.ReadUInt32();
            return new MailResultView(id, action, result, 0, item, r.ReadUInt32());
        }

        return new MailResultView(id, action, result, 0, 0, 0);
    });

    /// <summary>SMSG_MAIL_LIST_RESULT: only the letter count (the first byte).</summary>
    public static int MailListCount(byte[] payload) => payload.Length >= 1 ? payload[0] : throw new FormatException("MailListCount: empty body");

    public static DuelRequestedView DuelRequested(byte[] payload) => Decode(payload, nameof(DuelRequested), static data =>
    {
        var r = new PacketReader(data);
        ulong arbiter = r.ReadUInt64();
        return new DuelRequestedView(arbiter, r.ReadUInt64());
    });

    /// <summary>SMSG_DUEL_COMPLETE: u8 1 when the duel was fought out, 0 when it was interrupted.</summary>
    public static bool DuelComplete(byte[] payload) => payload.Length == 1 ? payload[0] != 0 : throw new FormatException("DuelComplete: body is not one byte");

    public static DuelWinnerView DuelWinner(byte[] payload) => Decode(payload, nameof(DuelWinner), static data =>
    {
        var r = new PacketReader(data);
        byte reason = r.ReadByte();
        string winner = r.ReadCString();
        return new DuelWinnerView(reason, winner, r.ReadCString());
    });

    public static LootResponseView LootResponse(byte[] payload) => Decode(payload, nameof(LootResponse), static data =>
    {
        var r = new PacketReader(data);
        ulong source = r.ReadUInt64();
        byte type = r.ReadByte();
        uint gold = r.ReadUInt32();
        byte count = r.ReadByte();
        var items = new List<LootItemView>(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = r.ReadByte();
            uint item = r.ReadUInt32();
            uint itemCount = r.ReadUInt32();
            uint display = r.ReadUInt32();
            r.Skip(8); // random suffix, random property
            items.Add(new LootItemView(slot, item, itemCount, display, r.ReadByte()));
        }

        return new LootResponseView(source, type, gold, items);
    });

    /// <summary>SMSG_LOOT_MONEY_NOTIFY: u32 this member's share.</summary>
    public static uint LootMoneyNotify(byte[] payload) => Decode(payload, nameof(LootMoneyNotify), static data => new PacketReader(data).ReadUInt32());

    public static AttackerStateView AttackerState(byte[] payload) => Decode(payload, nameof(AttackerState), static data =>
    {
        var r = new PacketReader(data);
        var hit = (HitInfo)r.ReadUInt32();
        ulong attacker = r.ReadPackedGuid();
        ulong victim = r.ReadPackedGuid();
        uint total = r.ReadUInt32();
        byte subCount = r.ReadByte();
        r.Skip(subCount * 20);
        var state = (VictimState)r.ReadUInt32();
        r.Skip(4); // attacker state
        uint spell = r.ReadUInt32();
        return new AttackerStateView(hit, attacker, victim, total, state, spell, r.ReadUInt32());
    });

    public static SpellGoView SpellGo(byte[] payload) => Decode(payload, nameof(SpellGo), static data =>
    {
        var r = new PacketReader(data);
        ulong item = r.ReadPackedGuid();
        ulong caster = r.ReadPackedGuid();
        uint spell = r.ReadUInt32();
        ushort flags = r.ReadUInt16();
        byte hitCount = r.ReadByte();
        var hits = new List<ulong>(hitCount);
        for (int i = 0; i < hitCount; i++) hits.Add(r.ReadUInt64());
        byte missCount = r.ReadByte();
        var misses = new List<(ulong, byte)>(missCount);
        for (int i = 0; i < missCount; i++)
        {
            ulong guid = r.ReadUInt64();
            byte reason = r.ReadByte();
            if (reason == (byte)SpellMissInfo.Reflect) r.ReadByte();
            misses.Add((guid, reason));
        }

        return new SpellGoView(item, caster, spell, flags, hits, misses);
    });

    public static CastResultView CastResult(byte[] payload) => Decode(payload, nameof(CastResult), static data =>
    {
        var r = new PacketReader(data);
        uint spell = r.ReadUInt32();
        bool success = r.ReadByte() == 0; // SpellCastResultStatus.Success
        return new CastResultView(spell, success, success ? SpellCastResult.CastOk : (SpellCastResult)r.ReadByte());
    });

    public static SpellFailureView SpellFailure(byte[] payload) => Decode(payload, nameof(SpellFailure), static data =>
    {
        var r = new PacketReader(data);
        ulong caster = r.ReadUInt64();
        uint spell = r.ReadUInt32();
        return new SpellFailureView(caster, spell, (SpellCastResult)r.ReadByte());
    });

    public static ChatMessageView ChatMessage(byte[] payload) => Decode(payload, nameof(ChatMessage), static data =>
    {
        var r = new PacketReader(data);
        var type = (ChatType)r.ReadByte();
        if (type is ChatType.Channel or ChatType.MonsterSay or ChatType.MonsterYell or ChatType.MonsterEmote
            or ChatType.MonsterWhisper or ChatType.RaidBossEmote or ChatType.RaidBossWhisper)
            throw new FormatException("ChatMessage: channel and monster chat use another layout");
        var language = (Language)r.ReadUInt32();
        ulong sender = r.ReadUInt64();
        if (type is ChatType.Say or ChatType.Party or ChatType.Yell) r.ReadUInt64();
        uint length = r.ReadUInt32();
        if (length == 0 || length > r.Remaining) throw new FormatException("ChatMessage: text length");
        string text = Encoding.UTF8.GetString(r.ReadBytes((int)length)[..^1]);
        return new ChatMessageView(type, language, sender, text, (ChatTag)r.ReadByte());
    });

    public static XpGainView XpGain(byte[] payload) => Decode(payload, nameof(XpGain), static data =>
    {
        var r = new PacketReader(data);
        ulong victim = r.ReadUInt64();
        uint total = r.ReadUInt32();
        bool kill = r.ReadByte() == 0;
        return new XpGainView(victim, total, kill, kill ? r.ReadUInt32() : 0);
    });

    public static QuestKillView QuestKill(byte[] payload) => Decode(payload, nameof(QuestKill), static data =>
    {
        var r = new PacketReader(data);
        uint quest = r.ReadUInt32();
        uint entry = r.ReadUInt32();
        uint count = r.ReadUInt32();
        uint required = r.ReadUInt32();
        return new QuestKillView(quest, entry, count, required, r.ReadUInt64());
    });

    public static QuestCompleteView QuestComplete(byte[] payload) => Decode(payload, nameof(QuestComplete), static data =>
    {
        var r = new PacketReader(data);
        uint quest = r.ReadUInt32();
        r.Skip(4); // 3
        uint xp = r.ReadUInt32();
        uint money = r.ReadUInt32();
        return new QuestCompleteView(quest, xp, money, r.ReadUInt32());
    });

    private delegate T Reader<T>(ReadOnlySpan<byte> data);

    private static T Decode<T>(byte[] payload, string what, Reader<T> read)
    {
        ArgumentNullException.ThrowIfNull(payload);
        try
        {
            return read(payload);
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new FormatException($"{what}: malformed {payload.Length}-byte body", error);
        }
    }
}

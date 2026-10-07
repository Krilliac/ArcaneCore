using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// One managed bot in scripted mode: the <see cref="IPlayerbotController"/> that replaces its brain, plus typed client
/// actions. Every action runs one ordinary CMSG through the real world handler on the world thread (the same admission
/// as the brain's actions) and is recorded in <see cref="Log"/>; the returned bool is handler admission only — the
/// outcome is established from the server's replies (<see cref="WaitForPacketAsync{T}"/>) or world state.
/// </summary>
public sealed class ScenarioBot : IPlayerbotController
{
    private readonly ScenarioContext _context;
    private volatile bool _detached;

    internal ScenarioBot(ScenarioContext context, string name)
    {
        _context = context;
        Name = name;
    }

    public string Name { get; }

    public Guid BotId { get; internal set; }

    /// <summary>The bot player's GUID (known after login).</summary>
    public ObjectGuid Guid { get; internal set; }

    public ScenarioPacketLog Log { get; } = new();

    /// <summary>Acknowledge server teleports and movement orders each tick like a client (default on).</summary>
    public bool AutoAcknowledge { get; set; } = true;

    public bool IsDetached => _detached;

    internal WorldSession? Session { get; set; }

    void IPlayerbotController.Tick(PlayerbotControllerContext context, uint elapsedMs)
    {
        // The observer already recorded everything; keep the bounded drain queue empty.
        context.Session.DrainManagedPackets();
        if (AutoAcknowledge) context.AcknowledgeServerOrders();
    }

    void IPlayerbotController.Detached(Guid botId)
    {
        _detached = true;
        if (Session is { } session) session.ManagedPacketObserver = null;
    }

    /// <summary>A mark for "packets from now on" (pass as <c>since</c>).</summary>
    public long Mark() => ScenarioPacketLog.NextSequence;

    /// <summary>Read this bot's player on the world thread.</summary>
    public Task<T> ReadAsync<T>(Func<Player, T> read) => _context.World.InvokeAsync(() => read(RequirePlayer()));

    /// <summary>Run one client opcode through its world handler (world thread). False: refused by the transport gate.</summary>
    public Task<bool> SendAsync(WorldOpcode opcode, byte[] payload)
        => _context.World.InvokeAsync(() => SendOnWorld(opcode, payload));

    // --- targeting and combat -------------------------------------------------------------------------------------

    public Task<bool> TargetAsync(ObjectGuid target) => SendAsync(WorldOpcode.CmsgSetSelection, ScenarioPackets.Guid(target.Value));

    /// <summary>CMSG_SET_SELECTION then CMSG_ATTACKSWING, as the client does.</summary>
    public Task<bool> AttackAsync(ObjectGuid target) => _context.World.InvokeAsync(() =>
        SendOnWorld(WorldOpcode.CmsgSetSelection, ScenarioPackets.Guid(target.Value))
        && SendOnWorld(WorldOpcode.CmsgAttackswing, ScenarioPackets.Guid(target.Value)));

    public Task<bool> StopAttackAsync() => SendAsync(WorldOpcode.CmsgAttackstop, []);

    public Task<bool> CastAsync(uint spellId, ObjectGuid? target = null)
        => SendAsync(WorldOpcode.CmsgCastSpell, target is { } unit
            ? ScenarioPackets.CastSpellAt(spellId, unit.Value)
            : ScenarioPackets.CastSpell(spellId, SpellCastTargets.ForSelf()));

    // --- loot -----------------------------------------------------------------------------------------------------

    public Task<bool> LootAsync(ObjectGuid source) => SendAsync(WorldOpcode.CmsgLoot, ScenarioPackets.Guid(source.Value));

    public Task<bool> LootMoneyAsync() => SendAsync(WorldOpcode.CmsgLootMoney, []);

    public Task<bool> LootItemAsync(byte slot) => SendAsync(WorldOpcode.CmsgAutostoreLootItem, ScenarioPackets.LootSlot(slot));

    public Task<bool> ReleaseLootAsync(ObjectGuid source) => SendAsync(WorldOpcode.CmsgLootRelease, ScenarioPackets.Guid(source.Value));

    public Task<bool> UseGameObjectAsync(ObjectGuid gameObject) => SendAsync(WorldOpcode.CmsgGameobjUse, ScenarioPackets.Guid(gameObject.Value));

    // --- group ----------------------------------------------------------------------------------------------------

    public Task<bool> InviteAsync(string name) => SendAsync(WorldOpcode.CmsgGroupInvite, ScenarioPackets.GroupInvite(name));

    public Task<bool> AcceptInviteAsync() => SendAsync(WorldOpcode.CmsgGroupAccept, []);

    public Task<bool> DeclineInviteAsync() => SendAsync(WorldOpcode.CmsgGroupDecline, []);

    public Task<bool> LeaveGroupAsync() => SendAsync(WorldOpcode.CmsgGroupDisband, []);

    public Task<bool> SetLootMethodAsync(LootMethod method, ObjectGuid masterLooter = default, uint threshold = Group.DefaultLootThreshold)
        => SendAsync(WorldOpcode.CmsgLootMethod, ScenarioPackets.LootMethod((uint)method, masterLooter.Value, threshold));

    // --- trade ----------------------------------------------------------------------------------------------------

    public Task<bool> InitiateTradeAsync(ObjectGuid with) => SendAsync(WorldOpcode.CmsgInitiateTrade, ScenarioPackets.Guid(with.Value));

    public Task<bool> BeginTradeAsync() => SendAsync(WorldOpcode.CmsgBeginTrade, []);

    /// <summary>Offer the carried item <paramref name="item"/> (its bag and slot are resolved on the world thread).</summary>
    public Task<bool> SetTradeItemAsync(byte tradeSlot, ObjectGuid item) => _context.World.InvokeAsync(() =>
    {
        Item found = RequirePlayer().Inventory.GetItemByGuid(item)
            ?? throw new ScenarioAssertionException($"{Name} does not carry item {item.Value:X}");
        return SendOnWorld(WorldOpcode.CmsgSetTradeItem, ScenarioPackets.SetTradeItem(tradeSlot, found.BagSlot, found.Slot));
    });

    public Task<bool> SetTradeGoldAsync(uint copper) => SendAsync(WorldOpcode.CmsgSetTradeGold, ScenarioPackets.UInt32(copper));

    public Task<bool> AcceptTradeAsync() => SendAsync(WorldOpcode.CmsgAcceptTrade, []);

    public Task<bool> CancelTradeAsync() => SendAsync(WorldOpcode.CmsgCancelTrade, []);

    // --- mail -----------------------------------------------------------------------------------------------------

    public Task<bool> SendMailAsync(ObjectGuid mailbox, string receiver, string subject, string body,
        ObjectGuid item = default, uint money = 0, uint cod = 0)
        => SendAsync(WorldOpcode.CmsgSendMail, ScenarioPackets.SendMail(mailbox.Value, receiver, subject, body, item.Value, money, cod));

    public Task<bool> GetMailListAsync(ObjectGuid mailbox) => SendAsync(WorldOpcode.CmsgGetMailList, ScenarioPackets.Guid(mailbox.Value));

    public Task<bool> TakeMailItemAsync(ObjectGuid mailbox, uint mailId)
        => SendAsync(WorldOpcode.CmsgMailTakeItem, ScenarioPackets.GuidUInt32(mailbox.Value, mailId));

    public Task<bool> TakeMailMoneyAsync(ObjectGuid mailbox, uint mailId)
        => SendAsync(WorldOpcode.CmsgMailTakeMoney, ScenarioPackets.GuidUInt32(mailbox.Value, mailId));

    // --- duel -----------------------------------------------------------------------------------------------------

    /// <summary>Spell 7266 "Duel" at the opponent (classic spell id; the bot must know it).</summary>
    public const uint DuelSpell = 7266;

    public Task<bool> RequestDuelAsync(ObjectGuid opponent, uint spellId = DuelSpell) => CastAsync(spellId, opponent);

    public Task<bool> AcceptDuelAsync(ulong arbiter) => SendAsync(WorldOpcode.CmsgDuelAccepted, ScenarioPackets.Guid(arbiter));

    public Task<bool> CancelDuelAsync(ulong arbiter) => SendAsync(WorldOpcode.CmsgDuelCancelled, ScenarioPackets.Guid(arbiter));

    // --- chat -----------------------------------------------------------------------------------------------------

    public Task<bool> SayAsync(string text) => ChatAsync(ChatType.Say, text);

    public Task<bool> PartyAsync(string text) => ChatAsync(ChatType.Party, text);

    public Task<bool> WhisperAsync(string to, string text) => ChatAsync(ChatType.Whisper, text, to);

    /// <summary>
    /// CMSG_MESSAGECHAT in the bot's own team language (Common / Orcish): the server refuses Universal outside AFK/DND
    /// replies (vmangos WorldSession::IsLanguageAllowedForChatType).
    /// </summary>
    public Task<bool> ChatAsync(ChatType type, string text, string? target = null) => _context.World.InvokeAsync(() =>
    {
        Language language = RequirePlayer().Team == Team.Horde ? Language.Orcish : Language.Common;
        return SendOnWorld(WorldOpcode.CmsgMessagechat, ScenarioPackets.Chat(type, language, text, target));
    });

    // --- quests ---------------------------------------------------------------------------------------------------

    public Task<bool> QuestHelloAsync(ObjectGuid giver) => SendAsync(WorldOpcode.CmsgQuestgiverHello, ScenarioPackets.Guid(giver.Value));

    public Task<bool> AcceptQuestAsync(ObjectGuid giver, uint quest)
        => SendAsync(WorldOpcode.CmsgQuestgiverAcceptQuest, ScenarioPackets.GuidUInt32(giver.Value, quest));

    public Task<bool> CompleteQuestAsync(ObjectGuid giver, uint quest)
        => SendAsync(WorldOpcode.CmsgQuestgiverCompleteQuest, ScenarioPackets.GuidUInt32(giver.Value, quest));

    public Task<bool> RequestQuestRewardAsync(ObjectGuid giver, uint quest)
        => SendAsync(WorldOpcode.CmsgQuestgiverRequestReward, ScenarioPackets.GuidUInt32(giver.Value, quest));

    public Task<bool> ChooseQuestRewardAsync(ObjectGuid giver, uint quest, uint choice = 0)
        => SendAsync(WorldOpcode.CmsgQuestgiverChooseReward, ScenarioPackets.QuestChooseReward(giver.Value, quest, choice));

    // --- area triggers --------------------------------------------------------------------------------------------

    public Task<bool> AreaTriggerAsync(uint triggerId) => SendAsync(WorldOpcode.CmsgAreatrigger, ScenarioPackets.UInt32(triggerId));

    // --- server replies -------------------------------------------------------------------------------------------

    /// <summary>
    /// Wait (bounded, through <see cref="ScenarioContext.WaitUntilAsync"/>) for a received <paramref name="opcode"/> at or after
    /// <paramref name="since"/> whose decoded body satisfies <paramref name="match"/>; returns the decoded body.
    /// </summary>
    public async Task<T> WaitForPacketAsync<T>(WorldOpcode opcode, Func<byte[], T> decode, Func<T, bool>? match = null,
        long since = 0, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(decode);
        T? found = default;
        await _context.WaitUntilAsync($"{Name} receives {opcode}", () =>
        {
            foreach (ScenarioPacket packet in Log.Received(opcode, since))
            {
                T value = decode(packet.Payload);
                if (match?.Invoke(value) ?? true)
                {
                    found = value;
                    return true;
                }
            }

            return false;
        }, timeout).ConfigureAwait(false);
        return found!;
    }

    /// <summary>Received packets of <paramref name="opcode"/> since a <see cref="Mark"/>, decoded.</summary>
    public IReadOnlyList<T> Received<T>(WorldOpcode opcode, Func<byte[], T> decode, long since = 0)
        => [.. Log.Received(opcode, since).Select(p => decode(p.Payload))];

    internal Player RequirePlayer()
        => Session?.Player is { IsInWorld: true } player ? player : throw new ScenarioAssertionException($"{Name} is not in the world");

    private bool SendOnWorld(WorldOpcode opcode, byte[] payload)
    {
        if (_detached || Session is not { } session) throw new ScenarioAssertionException($"{Name} is no longer scripted");
        Log.Record(ScenarioPacketDirection.Sent, _context.World.NowMs, opcode, payload);
        session.ManagedBudget = null;
        return session.TryManagedAction(opcode, payload);
    }
}

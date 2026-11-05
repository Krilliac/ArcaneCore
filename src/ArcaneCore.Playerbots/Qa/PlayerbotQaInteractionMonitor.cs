using System.Buffers.Binary;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Watches what the server refuses managed playerbots, and what it does not implement for them, and reports it straight to
/// <see cref="IPlayerbotQaSink"/> with no log line (docs/areas/playbots-qa.md, Interaction signals):
/// <list type="bullet">
/// <item><see cref="QaSignalKind.InteractionFailed"/>: a vendor, trainer, inventory or loot refusal (<see cref="OnInteractionFailed"/> from the game's decision
/// sites, <see cref="OnLootRefused"/> from CMSG_LOOT, the SMSG_INVENTORY_CHANGE_FAILURE a bot receives). A suspicious refusal is reported at once, an expected
/// one when the same bot gets it <see cref="RepeatThreshold"/> times within <see cref="RepeatWindowMs"/> (<see cref="PlayerbotQaInteractionRules"/>).</item>
/// <item><see cref="QaSignalKind.QuestBroken"/>: a refused quest exchange or a failed quest (<see cref="OnQuestRefused"/>, same classes), and an objective that
/// never progresses: <see cref="ObjectiveAttempts"/> kills of an objective creature, uses of an objective object or receipts of an objective item in a row
/// with no SMSG_QUESTUPDATE_ADD_KILL / ADD_ITEM for it.</item>
/// <item><see cref="QaSignalKind.ObjectUnsupported"/>, <see cref="QaSignalKind.ScriptUnsupported"/>, <see cref="QaSignalKind.OpcodeUnhandled"/>: a bot's use of a
/// game object the server does not implement, a creature AI or script gap, an opcode a bot sent that has no world handler.</item>
/// </list>
/// Every entry first asks whether QA is on and the unit is a managed playerbot (<see cref="PlayerbotQaBots.Identify"/>); a real player or a creature returns
/// there, so nothing a real player does is reported (a script gap has no player and is reported for the content). The packet tap
/// (<see cref="OnPacket"/>) runs for the packets a bot receives (the four it looks at even under Lean fidelity): it switches on the opcode first and returns for every other opcode, allocation-free;
/// the kill hook returns for a killer that is not a bot. The counters are value-keyed dictionaries under one lock, bounded by <see cref="MaxTracked"/>; a
/// signal is built only when one is reported, and each (bot, kind, reason, ids) at most once per <see cref="ThrottleMs"/>. World thread (the game's
/// sites, the handlers and the kill events run there; a packet sent from another thread is ignored by the stateful cases).
/// </summary>
internal sealed class PlayerbotQaInteractionMonitor : IGameQaObserver, IManagedPacketTap
{
    /// <summary>An expected refusal is reported once one bot got it this many times for the same thing ...</summary>
    internal const int RepeatThreshold = 5;

    /// <summary>... within this window (the bots back off a refused purchase, trainer or quest for 10 minutes, so a bot that learns never gets there).</summary>
    internal const long RepeatWindowMs = 600_000;

    /// <summary>Kills, uses or receipts of an objective in a row, by one bot, with no quest update, before the objective is reported as never progressing.</summary>
    internal const int ObjectiveAttempts = 5;

    /// <summary>The least time between two reports of the same thing by the same bot.</summary>
    internal const long ThrottleMs = 60_000;

    /// <summary>The counters are cleared when they hold more entries than this (a bound, not a target).</summary>
    internal const int MaxTracked = 8192;

    private const byte CreditKill = 0, CreditItem = 1;
    private const uint ObjectFlag = 0x80000000u;

    private readonly IPlayerbotQaSink _sink;
    private readonly Func<Unit?, QaBot?> _identify;
    private readonly Func<long> _clockMs;
    private readonly Func<QuestNpcServices?> _quests;
    private readonly object _gate = new();
    private readonly Dictionary<QaRepeatKey, long> _reported = [];
    private readonly Dictionary<QaRepeatKey, (int Count, long FirstMs)> _repeats = [];
    private readonly Dictionary<(ulong Bot, byte Credit, uint Entry), (int Count, uint Quest, int Objective)> _attempts = [];
    private readonly Dictionary<ulong, int> _explainedInventory = [];

    /// <summary>The production monitor: the QA sink, and the quest services looked up per use (they are built when the quest feature attaches).</summary>
    public PlayerbotQaInteractionMonitor(IPlayerbotQaSink sink, IServiceProvider services)
        : this(sink, quests: () => services.GetService<QuestNpcFeature>()?.Services)
    {
    }

    /// <param name="sink">Where the signals go.</param>
    /// <param name="identify">Test seam: which units are bots (default <see cref="PlayerbotQaBots.Identify"/>).</param>
    /// <param name="clockMs">Test seam: a millisecond clock (default <see cref="Environment.TickCount64"/>).</param>
    /// <param name="quests">The quest services (the bots' quest logs and the quest table); null when there are none.</param>
    internal PlayerbotQaInteractionMonitor(IPlayerbotQaSink sink, Func<Unit?, QaBot?>? identify = null, Func<long>? clockMs = null,
        Func<QuestNpcServices?>? quests = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _identify = identify ?? PlayerbotQaBots.Identify;
        _clockMs = clockMs ?? (static () => Environment.TickCount64);
        _quests = quests ?? (static () => null);
    }

    /// <summary>Whether the packet tap is worth installing: QA is on.</summary>
    public bool Active => _sink.Enabled;

    /// <summary>Objective counters held (a test reads it).</summary>
    internal int TrackedAttempts
    {
        get { lock (_gate) return _attempts.Count; }
    }

    // --- InteractionFailed ---------------------------------------------------------------------------------------------------------------------

    public void OnInteractionFailed(Player player, InteractionFailure failure, int code, uint npcEntry, uint item, uint spell)
    {
        if (!_sink.Enabled || _identify(player) is not { } bot) return;
        if (failure is InteractionFailure.BuyCannotStore or InteractionFailure.BuybackCannotStore)
        {
            // The equip error that follows explains this refusal; the packet tap must not count it a second time.
            lock (_gate)
            {
                if (_explainedInventory.Count >= MaxTracked) _explainedInventory.Clear();
                _explainedInventory[player.Guid.Value] = code;
            }
        }

        Interaction(bot, player, failure, code, npcEntry, 0, item, spell);
    }

    /// <summary>CMSG_LOOT of a bot was refused with <paramref name="result"/> (GameObjectLootHandlers).</summary>
    public void OnLootRefused(Player player, ObjectGuid source, LootResult result)
    {
        if (result == LootResult.Ok || !_sink.Enabled || _identify(player) is not { } bot) return;
        bool gameObject = source.High == HighGuid.GameObject;
        Interaction(bot, player, InteractionFailure.LootRefused, (int)result, gameObject ? 0 : source.Entry, gameObject ? source.Entry : 0, 0, 0);
    }

    private void Interaction(QaBot bot, Player player, InteractionFailure failure, int code, uint npc, uint gameObject, uint item, uint spell)
    {
        QaInteractionRule rule = PlayerbotQaInteractionRules.Of(failure, code);
        var key = new QaRepeatKey(player.Guid.Value, QaSignalKind.InteractionFailed, (int)failure, code, npc ^ (gameObject << 1), item, spell);
        if (!Admit(rule, key, out string repeats)) return;
        string codeName = PlayerbotQaInteractionRules.CodeName(rule.Token, code);
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.InteractionFailed, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Reason = rule.Token, Code = code,
            Npc = npc, GameObject = gameObject, Item = item, Spell = spell, Detail = repeats,
            Message = string.Create(CultureInfo.InvariantCulture, $"a bot's {rule.Token} request was refused")
                + (codeName.Length != 0 ? " with " + codeName : string.Empty) + (npc != 0 ? string.Create(CultureInfo.InvariantCulture, $" by creature {npc}") : string.Empty)
                + (gameObject != 0 ? string.Create(CultureInfo.InvariantCulture, $" at object {gameObject}") : string.Empty)
                + (item != 0 ? string.Create(CultureInfo.InvariantCulture, $" for item {item}") : string.Empty)
                + (spell != 0 ? string.Create(CultureInfo.InvariantCulture, $" for spell {spell}") : string.Empty)
                + (repeats.Length != 0 ? " (" + repeats + ")" : string.Empty),
        });
    }

    // --- QuestBroken ---------------------------------------------------------------------------------------------------------------------------

    public void OnQuestRefused(Player player, uint questId, QuestFailure failure, int code, string detail)
    {
        if (questId == 0 || !_sink.Enabled || _identify(player) is not { } bot) return;
        QaInteractionRule rule = PlayerbotQaInteractionRules.Of(failure, code);
        var key = new QaRepeatKey(player.Guid.Value, QaSignalKind.QuestBroken, (int)failure, code, questId, 0, 0);
        if (!Admit(rule, key, out string repeats)) return;
        string what = failure == QuestFailure.Unsupported && detail.Length != 0 ? " (missing " + detail + ")" : string.Empty;
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.QuestBroken, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Quest = questId, Reason = rule.Token, Code = code,
            Detail = repeats.Length != 0 ? repeats : detail,
            Message = string.Create(CultureInfo.InvariantCulture, $"quest {questId}: {rule.Words}")
                + (failure is QuestFailure.Invalid or QuestFailure.TurnInRefused ? " with " + PlayerbotQaInteractionRules.QuestReasonName(code) : string.Empty)
                + what + (repeats.Length != 0 ? " (" + repeats + ")" : string.Empty),
        });
    }

    /// <summary>
    /// A creature died (MapCombat.UnitKilled; <see cref="PlayerbotQaInteractionFeature"/> subscribes every map): a bot that killed a creature an incomplete
    /// kill objective of its log names makes one attempt of that objective. Returns at once for a killer that is not a bot, and on instance maps (a raid
    /// group credits raid quests only).
    /// </summary>
    internal void OnUnitKilled(Unit? killer, Unit victim)
    {
        if (killer is not Player player || victim is not Creature creature || !_sink.Enabled || _identify(player) is not { } bot
            || player.Map is not { InstanceId: 0 }) return;
        Objectives(bot, player, CreditKill, creature.Entry, creature.Entry);
    }

    public void OnObjectUsed(Player player, GameObject go)
    {
        if (!_sink.Enabled || _identify(player) is not { } bot) return;
        Objectives(bot, player, CreditKill, go.Entry | ObjectFlag, go.Entry);
    }

    /// <summary>
    /// One attempt of every incomplete objective of the bot's log that <paramref name="entry"/> credits (a kill objective, a game object objective with
    /// <see cref="ObjectFlag"/>, an item objective); allocation-free (the quest log is read by slot).
    /// </summary>
    private void Objectives(QaBot bot, Player player, byte credit, uint creditEntry, uint entry)
    {
        if (_quests() is not { } quests || quests.StateOf(player) is not { Loaded: true } state) return;
        QuestStore store = quests.Quests;
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint questId = state.Quests.SlotQuestId(slot);
            if (questId == 0 || state.Quests.Get(questId) is not { Status: QuestStatus.Incomplete } data || store.Get(questId) is not { } quest) continue;
            for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
            {
                bool match = credit == CreditItem
                    ? quest.ReqItemId[i] == entry && quest.ReqItemCount[i] != 0 && data.ItemCount[i] < quest.ReqItemCount[i] && entry != quest.Template.SrcItemId
                    : quest.ReqSpell[i] == 0 && quest.ReqCreatureOrGOId[i] == ((creditEntry & ObjectFlag) != 0 ? -(int)entry : (int)entry)
                        && data.CreatureOrGOCount[i] < quest.ReqCreatureOrGOCount[i];
                if (match) Attempt(bot, player, credit, creditEntry, entry, questId, i);
            }
        }
    }

    private void Attempt(QaBot bot, Player player, byte credit, uint creditEntry, uint entry, uint questId, int objective)
    {
        var key = (player.Guid.Value, credit, creditEntry);
        lock (_gate)
        {
            if (_attempts.Count >= MaxTracked) _attempts.Clear();
            (int count, uint quest, int index) = _attempts.GetValueOrDefault(key);
            count = quest == questId && index == objective ? count + 1 : 1;
            if (count < ObjectiveAttempts)
            {
                _attempts[key] = (count, questId, objective);
                return;
            }

            _attempts[key] = (0, questId, objective);
            if (!Due(new QaRepeatKey(player.Guid.Value, QaSignalKind.QuestBroken, 100 + credit, objective, questId, creditEntry, 0), _clockMs())) return;
        }

        bool item = credit == CreditItem;
        bool gameObject = !item && (creditEntry & ObjectFlag) != 0;
        string token = item ? PlayerbotQaInteractionRules.NoProgressItem : gameObject ? PlayerbotQaInteractionRules.NoProgressObject : PlayerbotQaInteractionRules.NoProgressKill;
        string noun = item ? "receipts of item" : gameObject ? "uses of object" : "kills of creature";
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.QuestBroken, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Quest = questId, Reason = token, EffectIndex = objective,
            Npc = item || gameObject ? 0 : entry, GameObject = gameObject ? entry : 0, Item = item ? entry : 0,
            Detail = string.Create(CultureInfo.InvariantCulture, $"{ObjectiveAttempts} {noun} {entry} in a row"),
            Message = string.Create(CultureInfo.InvariantCulture,
                $"quest {questId} objective {objective}: {ObjectiveAttempts} {noun} {entry} by one bot in a row, and no quest update for it"),
        });
    }

    /// <summary>A quest update for the bot: the objective it credits progressed, so its attempts start again.</summary>
    private void Credited(Player player, byte credit, uint creditEntry)
    {
        lock (_gate)
        {
            var key = (player.Guid.Value, credit, creditEntry);
            if (_attempts.ContainsKey(key)) _attempts.Remove(key);
        }
    }

    // --- ObjectUnsupported, ScriptUnsupported, OpcodeUnhandled ----------------------------------------------------------------------------------

    public void OnObjectUnsupported(Unit? user, GameObject go, string gap, uint value)
    {
        if (!_sink.Enabled || _identify(user) is not { } bot || user is not Player player) return;
        lock (_gate)
        {
            if (!Due(new QaRepeatKey(0, QaSignalKind.ObjectUnsupported, (int)go.Type, StringComparer.Ordinal.GetHashCode(gap), go.Entry, value, 0), _clockMs())) return;
        }

        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.ObjectUnsupported, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, GameObject = go.Entry, Code = (int)go.Type,
            Reason = gap, Detail = value != 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty, HasPosition = true, Map = player.Map?.MapId ?? 0,
            X = go.X, Y = go.Y, Z = go.Z,
            Message = string.Create(CultureInfo.InvariantCulture, $"a bot used game object {go.Entry} (type {(int)go.Type} {go.Type}): {gap} is not implemented")
                + (value != 0 ? string.Create(CultureInfo.InvariantCulture, $" (value {value})") : string.Empty),
        });
    }

    public void OnCreatureAiGap(Creature? creature, uint entry, string gap, uint value, string detail)
    {
        if (!_sink.Enabled) return;
        lock (_gate)
        {
            if (!Due(new QaRepeatKey(0, QaSignalKind.ScriptUnsupported, StringComparer.Ordinal.GetHashCode(gap), 0, entry, value, 0), _clockMs())) return;
        }

        // The bot the creature fights, if any, is the one that met the gap; the finding is the content's either way (bot-independent).
        QaBot? bot = creature is not null ? _identify(creature.Combat.Victim) : null;
        bool where = creature?.Map is not null;
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.ScriptUnsupported, AtUtc = DateTime.UtcNow, BotKey = bot?.Key, BotClass = bot?.Class ?? 0, Npc = entry, Reason = gap, Target = value,
            Detail = detail, HasPosition = where, Map = creature?.Map?.MapId ?? 0, X = creature?.X ?? 0, Y = creature?.Y ?? 0, Z = creature?.Z ?? 0,
            Message = string.Create(CultureInfo.InvariantCulture, $"creature {entry}: {gap}")
                + (value != 0 ? string.Create(CultureInfo.InvariantCulture, $" {value}") : string.Empty) + (detail.Length != 0 ? " (" + detail + ")" : string.Empty),
        });
    }

    public void OnUnhandledOpcode(WorldSession session, WorldOpcode opcode, bool sessionOnly)
    {
        if (!_sink.Enabled || _identify(session.Player) is not { } bot) return;
        lock (_gate)
        {
            if (!Due(new QaRepeatKey(0, QaSignalKind.OpcodeUnhandled, (int)opcode, 0, 0, 0, 0), _clockMs())) return;
        }

        string name = WorldOpcodeNames.GetName(opcode);
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.OpcodeUnhandled, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Code = (int)opcode,
            Reason = sessionOnly ? "session-only" : "no-handler", Detail = name,
            Message = sessionOnly
                ? $"a bot sent {name}, whose handler runs on the session task only: the managed path refuses it"
                : $"a bot sent {name}, which has no handler",
        });
    }

    // --- the packet tap ------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A packet a managed bot session captured (WorldSession.CaptureManagedPacket, under its send lock). Every opcode but four returns at the switch.
    /// </summary>
    public void OnPacket(WorldSession session, WorldOpcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case WorldOpcode.SmsgQuestupdateAddKill or WorldOpcode.SmsgQuestupdateAddItem or WorldOpcode.SmsgItemPushResult
                or WorldOpcode.SmsgInventoryChangeFailure:
                // Only the world thread touches the bot's quest log and inventory; a packet sent from a session task is not looked at.
                if (session.World.IsWorldThread && session.Player is { } player) OnPlayerPacket(player, opcode, payload);
                return;
            default:
                return;
        }
    }

    /// <summary>A packet <paramref name="player"/> received, on the world thread (<see cref="OnPacket"/>; tests call it from a session of their own).</summary>
    internal void OnPlayerPacket(Player player, WorldOpcode opcode, byte[] payload)
    {
        switch (opcode)
        {
            case WorldOpcode.SmsgQuestupdateAddKill:
                if (payload.Length >= 8) Credited(player, CreditKill, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4)));
                return;
            case WorldOpcode.SmsgQuestupdateAddItem:
                if (payload.Length >= 4) Credited(player, CreditItem, BinaryPrimitives.ReadUInt32LittleEndian(payload));
                return;
            case WorldOpcode.SmsgItemPushResult:
                // guid u64, received u32, created u32, shown u32, bag u8, slot u32, entry u32 (ItemPackets.ItemPushResult).
                if (payload.Length >= 29 && _sink.Enabled && BinaryPrimitives.ReadUInt64LittleEndian(payload) == player.Guid.Value && _identify(player) is { } bot)
                {
                    uint item = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(25));
                    Objectives(bot, player, CreditItem, item, item);
                }

                return;
            case WorldOpcode.SmsgInventoryChangeFailure:
                if (payload.Length >= 1 && payload[0] != 0 && _sink.Enabled) InventoryFailure(player, payload);
                return;
            default:
                return;
        }
    }

    private void InventoryFailure(Player player, byte[] payload)
    {
        int code = payload[0];
        lock (_gate)
        {
            if (_explainedInventory.TryGetValue(player.Guid.Value, out int explained) && explained == code)
            {
                _explainedInventory.Remove(player.Guid.Value); // a purchase that could not be stored, reported by the vendor
                return;
            }
        }

        if (_identify(player) is not { } bot) return;
        int at = code == (int)InventoryResult.CantEquipLevelI ? 5 : 1;
        ObjectGuid first = payload.Length >= at + 8 ? new ObjectGuid(BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(at))) : ObjectGuid.Empty;
        uint item = first.IsEmpty ? 0 : player.Inventory.GetItemByGuid(first)?.Entry ?? 0;
        Interaction(bot, player, InteractionFailure.InventoryChangeFailed, code, 0, 0, item, 0);
    }

    // --- shared ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a refusal is reported now: an expected one when it is the <see cref="RepeatThreshold"/>th of the bot within <see cref="RepeatWindowMs"/>
    /// (<paramref name="repeats"/> says how often), a suspicious one at once; both at most once per <see cref="ThrottleMs"/>.
    /// </summary>
    private bool Admit(QaInteractionRule rule, QaRepeatKey key, out string repeats)
    {
        repeats = string.Empty;
        long now = _clockMs();
        int count;
        long first;
        lock (_gate)
        {
            if (rule.Class == QaRefusalClass.Expected)
            {
                if (_repeats.Count >= MaxTracked) _repeats.Clear();
                (count, first) = _repeats.GetValueOrDefault(key);
                if (count == 0 || now - first > RepeatWindowMs)
                {
                    count = 0;
                    first = now;
                }

                count++;
                if (count < RepeatThreshold)
                {
                    _repeats[key] = (count, first);
                    return false;
                }

                _repeats[key] = (0, now);
            }
            else
            {
                count = 0;
                first = now;
            }

            if (!Due(key, now)) return false;
        }

        if (rule.Class == QaRefusalClass.Expected)
        {
            repeats = string.Create(CultureInfo.InvariantCulture, $"{count} times in {Math.Max(1, (now - first) / 1000)} s");
        }

        return true;
    }

    /// <summary>Whether <paramref name="key"/> was not reported within <see cref="ThrottleMs"/>; if so it is marked reported now. Under the lock.</summary>
    private bool Due(QaRepeatKey key, long now)
    {
        if (_reported.TryGetValue(key, out long last) && now - last < ThrottleMs) return false;
        if (_reported.Count >= MaxTracked) _reported.Clear();
        _reported[key] = now;
        return true;
    }
}

/// <summary>What a repeat counter or a throttle is about: a bot (0 for a bot-independent report), a kind, a reason, a code and up to three ids.</summary>
internal readonly record struct QaRepeatKey(ulong Bot, QaSignalKind Kind, int Reason, int Code, uint A, uint B, uint C);

using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots.Progression;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// Drives one managed bot while it is in a real player's group, instead of <see cref="PlayerbotBrain"/> (vmangos
/// src/game/PlayerBots/PartyBotAI.cpp; the player-facing actions of mangoszero's playerbot module). World thread only.
/// <para>
/// <b>Intake</b> (<see cref="Intake"/>, every tick for a grouped bot, once per think interval for an autonomous bot in no group):
/// only <see cref="IntakeOpcodes"/> are drained from the bot's capture queue. An invitation is accepted or declined by
/// <see cref="PlayerbotPartyOptions.InvitePolicy"/> (a pending invitation whose packet the bounded capture queue dropped is answered
/// all the same); the master's whispered or party-chat commands are obeyed and a stranger's whisper gets one polite, rate-limited
/// answer; a group loot roll is answered at once (<see cref="PlayerbotPartyOptions.LootRoll"/>); a group member's resurrection is accepted.
/// </para>
/// <para>
/// <b>Driving</b> (<see cref="Drives"/>, then <see cref="Update"/>): while the bot's group has a master (the leader, or the first real
/// player when the leader is a bot) this AI drives the bot. It follows at 2-5 yards and catches up when it cannot simply walk
/// (<see cref="PlayerbotFollower"/>, <c>World:Playerbots:Follow</c>: by default it teleports, as vmangos does, to a master more than 100
/// yards away or on another map, and also to one it has no reasonable way to, through the teleport service the GM .goname/.namego
/// commands use with its instance checks; it drops to a master below when the fall is survivable), fights what
/// the master orders, what the master fights and what attacks the group, eats and drinks, and releases any round-robin loot it holds
/// so the players can take it, whoever made the kill. A dead bot accepts a member's resurrection, revives in place when vmangos
/// ShouldAutoRevive allows it, or runs back (<see cref="PlayerbotRecovery"/>). When the master has been offline or gone for
/// <see cref="PlayerbotPartyOptions.MasterTimeoutSeconds"/> the bot leaves the group and the brain takes over again.
/// </para>
/// </summary>
internal sealed class PlayerbotPartyAI
{
    /// <summary>The only packets the party AI takes from the bot's capture queue (never an unfiltered drain).</summary>
    internal static readonly WorldOpcode[] IntakeOpcodes =
    [
        WorldOpcode.SmsgGroupInvite, WorldOpcode.SmsgMessagechat, WorldOpcode.SmsgLootStartRoll, WorldOpcode.SmsgResurrectRequest,
        WorldOpcode.SmsgTradeStatus, WorldOpcode.SmsgTextEmote, WorldOpcode.SmsgLootRollWon,
    ];

    /// <summary>The brain's melee approach distance (PlayerbotBrain: chase until within 4 yards, then swing).</summary>
    internal const float MeleeReach = 4f;

    /// <summary>A resting bot gets up and follows when its master is farther than this.</summary>
    internal const float RestBreakDistance = 30f;

    /// <summary>A kiting party bot ends its steps within this many yards of its master.</summary>
    internal const float KiteLeashYards = 30f;

    /// <summary>How long a dead bot waits for a resurrection (vmangos waits while ShouldAutoRevive says no) before it runs back.</summary>
    internal const long DefaultDeadWaitMs = 120_000;

    /// <summary>
    /// Round-robin loot the bot holds is released when its corpse is this close: the group reward distance (vmangos
    /// IsAtGroupRewardDistance, 74 yards), within which the bot was when the loot was given to it.
    /// </summary>
    internal const float RoundRobinReach = 74f;

    /// <summary>A corpse whose loot the bot could not release after this many tries (walk failed, or the open was refused) is left.</summary>
    internal const int MaxReleaseAttempts = 3;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;

    /// <summary>The bots' social memory seam (set by the feature; null in a host or a test without it).</summary>
    internal BotSocial.PlayerbotSocialRecorder? Social { get; set; }

    /// <summary>The world's trade desk, which drives the bot's deal windows (set by the feature; null in a host or a test without it).</summary>
    internal BotSocial.PlayerbotTradeDesk? TradeDesk { get; set; }

    /// <summary>Who owns the bot's one trade slot (the feature's registry), or null in a host without the playerbot feature: each driver is then the sole owner.</summary>
    private BotSocial.PlayerbotTradeOwners? Owners => BotSocial.PlayerbotTradeOwners.Of(_session);

    /// <summary>Who the bot is trading with, from the begin-trade packet or the bot's own request; empty when no trade is open.</summary>
    private ObjectGuid _tradePartner;
    private bool _tradeCounts; // the window with _tradePartner is worth a TradeCompleted note (set on the partner's accept, read on completion)
    private readonly PlayerbotCombatSpells _combatSpells;
    private readonly PlayerbotKite _kite;
    private readonly PlayerbotEquipment _equipment;
    private readonly PlayerbotRecovery _recovery;
    private readonly PlayerbotRisk _risk;
    private string? _wipe;
    private readonly PlayerbotReplyLimiter _replies = new();
    private readonly Random _random;
    private ObjectGuid _master;
    private ObjectGuid _leash;
    private string _masterName = string.Empty;
    private bool _engaged;
    private long _masterLostMs = -1;
    private PlayerbotPartyMode _mode;
    private bool _comeRequested;
    private ObjectGuid _ordered;

    /// <summary>How long a fight target with no path is skipped when the bot chooses a target itself, as the brain's refused chase (<c>UnreachableTargetMs</c>).</summary>
    internal const uint UnreachableTargetMs = 30_000;

    /// <summary>The targets this bot found no path to, skipped for <see cref="UnreachableTargetMs"/> in <see cref="SelectTarget"/>; the leader's explicit choices are exempt. One thread (the bot's think).</summary>
    private readonly PlayerbotUnreachableTargets _unreachable = new();
    private PlayerbotRoute? _routeSlot;

    /// <summary>The route this driver walks. Letting go of one (null or another route) marks it released
    /// (<see cref="PlayerbotRoute.Released"/>): <see cref="PlayerbotMotion.SettleAfterDriver"/> stops a walk nobody holds.</summary>
    private PlayerbotRoute? _route
    {
        get => _routeSlot;
        set
        {
            if (ReferenceEquals(_routeSlot, value)) return;
            if (_routeSlot is { } old) old.Released = true;
            _routeSlot = value;
        }
    }

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this bot's plans.</summary>
    private TileWait _tileWait;
    private Vector3 _routeGoal;
    private float _followAngle;
    private float _followDistance = 3f;
    private Unit? _target;
    private bool _attacking;
    private uint _thinkElapsed;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private long _deadSinceMs = -1;
    private bool _deathRetired;
    private bool _corpseRun;
    private long _resurrectAtMs;
    private uint _rangedIdleMs;
    private readonly Dictionary<ObjectGuid, int> _releaseTries = [];
    private Vector3? _stayPoint;
    private readonly PlayerbotFollower _follower;
    private uint _idleIntakeAtMs;
    private bool _idleIntakeStarted;
    private readonly Chat.PlayerbotChat? _chat;
    private readonly Guid _botId;
    private readonly Chat.PlayerbotChatEvents _chatEvents = new();
    private readonly PlayerbotFollowDropWatch _followDrop = new();
    private readonly Dictionary<ObjectGuid, long> _emoteReplies = [];
    private PlayerbotEpisode _deadEpisode;
    private PlayerbotEpisode _inviteEpisode;
    private PlayerbotEpisode _orphanEpisode;

    internal PlayerbotPartyAI(WorldSession session, PlayerbotOptions options, Chat.PlayerbotChat? chat = null, Guid botId = default,
        PlayerbotEquipment? equipment = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _combatSpells = new PlayerbotCombatSpells(session);
        _kite = new PlayerbotKite(session, options, _combatSpells);
        _equipment = equipment ?? new PlayerbotEquipment(session);
        _recovery = new PlayerbotRecovery(session, options);
        _risk = new PlayerbotRisk(session, options, _combatSpells) { AvoidsStrongCreatures = false };
        _follower = new PlayerbotFollower(session, options);
        _random = new Random(session.AccountId);
        _chat = chat;
        _botId = botId;
        _chatEvents.Items = entry => _session.Services.GetService<IItemTemplateStore>()?.Find(entry);
        _chatEvents.MapKind = mapId => Game.Maps.Templates.WorldMaps.Of(_session.World).Registry.Find(mapId) is { Instanceable: true } map
            ? (map.IsBattleground ? 2 : 1, map.Name)
            : (0, null);
    }

    /// <summary>The bot's remembered events for its chat (the persona, and the conversation director's topics).</summary>
    internal Chat.PlayerbotChatEvents ChatEvents => _chatEvents;

    /// <summary>The last resurrection cast (inspection and tests: the dead member's name).</summary>
    internal string? LastResurrection { get; private set; }

    /// <summary>Whether that cast was aimed at the member's corpse (it had released) rather than at its unreleased body.</summary>
    internal bool LastResurrectionAtCorpse { get; private set; }

    /// <summary>The bot's combat spells (inspection and tests).</summary>
    internal PlayerbotCombatSpells CombatSpells => _combatSpells;

    /// <summary>
    /// What the bot does when it is not party-driven (the brain's goal and quest; set by <see cref="ManagedPlayerbotFeature"/>), for
    /// the chat persona. Null: the party AI's own goal only.
    /// </summary>
    internal Func<(PlayerbotGoalKind Goal, uint QuestId)>? BrainGoal { get; set; }

    /// <summary>
    /// Live internal diagnostic snapshot callback supplied by <see cref="ManagedPlayerbotFeature"/> (botId, driver, goal, stall, home,
    /// risk, motion/route, and brain trace ring buffer) for GM debug inspection.
    /// </summary>
    internal Func<Player, string>? BrainDiagnostics { get; set; }

    private static bool IsGm(Player sender)
        => sender.IsGameMaster || sender.Session.Security >= ArcaneCore.Kernel.Accounts.AccountSecurity.GameMaster;

    /// <summary>
    /// An invitation the bot accepts whatever <see cref="PlayerbotPartyOptions.InvitePolicy"/> says (bot, inviter): another bot's
    /// invitation into a group the coordinator formed for both (<see cref="Groups.PlayerbotGroupCoordinator"/>). Null: none.
    /// </summary>
    internal Func<Player, Player, bool>? AcceptsBotGroup { get; set; }

    /// <summary>
    /// What the bot's memory and goal say about an inviter (bot, inviter), for <see cref="PlayerbotInvitePolicy.Affinity"/> (set by
    /// <see cref="ManagedPlayerbotFeature"/>). Null: every inviter is a stranger.
    /// </summary>
    internal Func<Player, Player, PlayerbotGroupInvites.Standing>? InviteStanding { get; set; }

    /// <summary>
    /// The vote of a bot in a bot-led group on a group loot roll (bot, item): need what it would wear, greed the rest. Null, or a null
    /// answer: <see cref="PlayerbotPartyOptions.LootRoll"/>.
    /// </summary>
    internal Func<Player, uint, RollVote?>? GroupLootVote { get; set; }

    /// <summary>
    /// A bot-led group that owns the bot after its master handed the lead to a bot (PlayerbotGroupCoordinator.OwnsAgainst): asked first with
    /// the master's order (null: no order), true while it keeps driving the bot, so the party AI does not engage (and stop) it. An order
    /// that moves or fights takes the lead back, and the party AI then carries it out.
    /// </summary>
    internal Func<Player, Player, PlayerbotPartyCommand?, bool>? GroupOwns { get; set; }

    /// <summary>Whether the party AI drives the bot now (it has a master, or is waiting out a departed one).</summary>
    internal bool IsEngaged => _engaged;

    /// <summary>The master's GUID while engaged, otherwise empty.</summary>
    internal ObjectGuid Master => _master;

    internal string MasterName => _masterName;

    internal PlayerbotPartyMode Mode => _mode;

    /// <summary>Whether an operator leashed the bot to a player (<see cref="Leash"/>): it follows or holds without a party.</summary>
    internal bool IsLeashed => !_leash.IsEmpty;

    /// <summary>
    /// <c>.playerbot follow|stay</c>: make <paramref name="master"/> the bot's master without a group. The party AI then drives the
    /// bot exactly as in a group (follow at 2-5 yards, catch up through <see cref="PlayerbotFollower"/> under its map policy, fight back and, in Stay, only
    /// its own attackers), and the master's whispered orders are obeyed. The leash ends with <see cref="Unleash"/>, when the master
    /// goes offline (<see cref="Drives"/> releases a bot that has no master and no group) or when the master whispers 'leave'.
    /// World thread.
    /// </summary>
    internal void Leash(Player player, Player master, bool stay)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(master);
        _leash = master.Guid;
        Engage(player, master.Guid);
        if (stay)
        {
            _mode = PlayerbotPartyMode.Stay;
            _stayPoint = null; // taken where the bot comes to a stop
            PlayerbotMovementControl.Stop(_session, player);
        }
    }

    /// <summary>Drop the leash (<c>.playerbot dismiss</c>); false when the bot was not leashed. The next think hands it back to its brain.</summary>
    internal bool Unleash(Player player)
    {
        if (_leash.IsEmpty) return false;
        Release(player);
        return true;
    }

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Follow;

    internal uint TargetEntry { get; private set; }

    internal Creature? InspectionTarget => _target as Creature;

    /// <summary>The unit the bot fights, a creature or a player (inspection and tests).</summary>
    internal Unit? InspectionUnit => _target;

    /// <summary>How the bot follows its master and catches up (<see cref="PlayerbotFollower"/>; inspection and tests).</summary>
    internal PlayerbotFollower Follower => _follower;

    /// <summary>
    /// How long a dead bot waits for a resurrection before it runs back to its body (<see cref="DefaultDeadWaitMs"/>; a test seam,
    /// never configuration-bound).
    /// </summary>
    internal long DeadWaitMs { get; set; } = DefaultDeadWaitMs;

    private PartyServices? _services;
    private PartyServices Services => _services ??= new(_session);

    private long _clockOffsetMs;

    /// <summary>Test seam (never configuration-bound): milliseconds added to the AI's clock (and its follower's), so a test lets minutes pass between thinks.</summary>
    internal long ClockOffsetMs
    {
        get => _clockOffsetMs;
        set
        {
            _clockOffsetMs = value;
            _follower.ClockOffsetMs = value;
        }
    }

    private long Now => (long)_session.World.Uptime.TotalMilliseconds + _clockOffsetMs;

    // --- intake ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Take this tick's invitations, chat lines, loot rolls and resurrection offers out of the bot's capture queue and answer them
    /// (world thread; every tick for every autonomous bot). The answers are a client's own replies: they do not wait for the shared
    /// action budget.
    /// </summary>
    internal void Intake(Player player)
    {
        // A bot with a trade deal is driven on every tick, ahead of the idle-intake throttle below (the global fast path is one volatile read).
        if (TradeDesk is { Count: > 0 } desk && desk.HasDeal(_botId)) DriveDeal(player, desk);
        if (_chat is { IsActive: true }) _chatEvents.Observe(player, Now);
        // A bot outside any group only needs invitations and strangers' whispers answered: once per think interval is soon enough,
        // and it keeps a large fleet of solo bots from draining their queues every tick (the invitation itself is read from the
        // group state below, so a dropped SMSG_GROUP_INVITE costs nothing). A grouped bot reads every tick: its master's orders.
        if (!_engaged && Services.Social?.Groups.GetGroup(player.Guid) is null)
        {
            uint nowMs = _session.World.NowMs;
            if (_idleIntakeStarted && unchecked((int)(nowMs - _idleIntakeAtMs)) < _options.ThinkIntervalMs) return;
            _idleIntakeStarted = true;
            _idleIntakeAtMs = nowMs;
        }

        IReadOnlyList<ManagedSessionPacket> packets = _session.DrainManagedPackets(IntakeOpcodes);
        foreach (ManagedSessionPacket packet in packets)
        {
            switch (packet.Opcode)
            {
                case WorldOpcode.SmsgGroupInvite: OnInvite(player, packet.Payload); break;
                case WorldOpcode.SmsgMessagechat: OnChat(player, packet.Payload); break;
                case WorldOpcode.SmsgLootStartRoll: OnLootRoll(player, packet.Payload); break;
                case WorldOpcode.SmsgLootRollWon: OnLootRollWon(player, packet.Payload); break;
                case WorldOpcode.SmsgResurrectRequest: OnResurrectRequest(player, packet.Payload); break;
                case WorldOpcode.SmsgTradeStatus: OnTradeStatus(player, packet.Payload); break;
                case WorldOpcode.SmsgTextEmote: OnTextEmote(player, packet.Payload); break;
            }
        }

        // The capture queue is bounded (drop-oldest): an invitation still pending without its packet is answered as from the leader.
        if (Services.Social?.Groups.GetInvite(player.Guid) is not null) OnInvite(player, []);
        // A quest a group member shared (CMSG_PUSHQUESTTOPARTY): the offer is the server's pending share (vmangos m_questShareInfo); the
        // SMSG_QUESTGIVER_QUEST_DETAILS that came with it is left in the queue for the brain's own quest exchanges, which ignore a player.
        if (Services.Quests?.ShareInfoOf(player) is { } offer) OnQuestShare(player, offer);
    }

    /// <summary>
    /// Answer a shared quest as a client does from its quest window: accept it (CMSG_QUESTGIVER_ACCEPT_QUEST with the sharer's guid,
    /// vmangos <c>HandleQuestgiverAcceptQuestOpcode</c> with a player as giver) when the sharer is in the bot's group and the bot can
    /// take a quest the server settles; otherwise decline (MSG_QUEST_PUSH_RESULT, QUEST_PARTY_MSG_DECLINE_QUEST, vmangos
    /// <c>HandleQuestPushResult</c>), which clears the offer either way.
    /// </summary>
    private void OnQuestShare(Player player, ArcaneCore.Game.Npc.QuestShareInfo offer)
    {
        ArcaneCore.Game.Npc.QuestNpcServices quests = Services.Quests!;
        bool grouped = Services.Social?.Groups is { } groups && groups.AreInSameGroup(player.Guid, offer.Sharer);
        var writer = new PacketWriter(13);
        writer.WriteUInt64(offer.Sharer.Value);
        if (grouped && quests.IsRewardable(offer.QuestId) && quests.CanTakeQuest(player, offer.QuestId) == true)
        {
            writer.WriteUInt32(offer.QuestId);
            Act(WorldOpcode.CmsgQuestgiverAcceptQuest, writer.ToArray(), budgeted: false);
        }
        else
        {
            writer.WriteByte((byte)ArcaneCore.Game.Quests.QuestShareMessage.DeclineQuest);
            Act(WorldOpcode.MsgQuestPushResult, writer.ToArray(), budgeted: false);
        }
    }

    private void OnInvite(Player player, byte[] payload)
    {
        if (Services.Social is not { } social || social.Groups.GetInvite(player.Guid) is not { } group)
        {
            _inviteEpisode.Reset();
            return; // already answered
        }

        string? name = PlayerbotGroupInvites.ReadInviter(payload);
        Player? inviter = (name is null ? null : _session.World.FindOnlinePlayer(name)) ?? _session.World.FindOnlinePlayer(group.LeaderGuid);
        PlayerbotPartyOptions party = _options.Party;
        bool accept = inviter is not null && (AcceptsBotGroup?.Invoke(player, inviter) == true || PlayerbotGroupInvites.Allows(party.InvitePolicy, party.Allowlist, inviter.Name,
            PlayerbotGroupInvites.SameGuild(social, player, inviter), PlayerbotGroupInvites.OnBotsFriendList(social, player, inviter),
            InviteStanding?.Invoke(player, inviter)));
        bool sent = Act(accept ? WorldOpcode.CmsgGroupAccept : WorldOpcode.CmsgGroupDecline, [], budgeted: false);
        // Outcome: the answer clears the invitation (Accept and Decline both do). One still pending after it, tick after tick, was never answered.
        if (social.Groups.GetInvite(player.Guid) is null) _inviteEpisode.Reset();
        else if (_inviteEpisode.Observe(true, Now, PlayerbotPartyAnomalies.InviteAnswerBudgetMs, PlayerbotPartyAnomalies.MaxGapMs))
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.InviteUnanswered, string.Create(CultureInfo.InvariantCulture,
                $"invitation from {inviter?.Name ?? "an unknown inviter"} still pending {PlayerbotPartyAnomalies.Seconds(_inviteEpisode.ElapsedMs(Now))}s after the bot answered it ({(accept ? "accept" : "decline")}, dispatched {sent})"));
    }

    private void OnChat(Player player, byte[] payload)
    {
        if (!PlayerbotChatCommands.TryRead(payload, out PlayerbotChatLine line, out bool malformed))
        {
            if (malformed)
                PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.ChatCommandMalformed,
                    string.Create(CultureInfo.InvariantCulture, $"malformed SMSG_MESSAGECHAT ({payload.Length} bytes)"));
            return;
        }
        Player? sender = _session.World.FindOnlinePlayer(line.Sender);
        bool senderIsBot = sender?.Session is WorldSession { IsManaged: true };
        bool isGm = sender is not null && IsGm(sender);
        ObjectGuid master = ResolveMaster(player);
        // "rivals off", "rivals on" and "rivals": a real player's whisper to any bot, taken before the ignore list and the hearers (a player the bot
        // ignores can still opt out, and the chat service never hears the line). The answer is a system message, so it works with bot chat off.
        if (sender is not null && !senderIsBot && sender.Guid != player.Guid && line.Type == ChatType.Whisper && Social?.RivalryOptOut is { } optOut
            && optOut.Handle(sender.Guid.Value, sender.AccountId, line.Text, Now, _options.Social.Rivalry) is { Consumed: true } rivalsLine)
        {
            if (rivalsLine.Answer is not null) Gm.Core.GmReplies.SendSystemMessage(sender, rivalsLine.Answer);
            return;
        }

        // A player on the bot's ignore list is not heard at all, as a client hides an ignored player's chat: no reply, no listener (so no
        // affinity from the talk). The master and a game master are never dropped.
        if (sender is not null && sender.Guid != player.Guid && sender.Guid != master && !isGm
            && Services.Social?.Friends.HasIgnore(player, sender.Guid) == true)
            return;
        // Every line heard is input for the chat service's listeners (later lanes react to channel talk, needs, LFG calls), bots' lines too.
        // A line the bot voice answered itself (an "inv" to its LFG call, a price question to its WTS call) gets no other reply; the
        // master's lines are never taken from the orders.
        if (_chat is { IsActive: true, HasHearers: true } hearer && sender is not null && line.Sender != player.Guid
            && hearer.Hear(new Chat.BotHeardLine(_botId, player.Guid, player.Name, line.Type, line.ChannelName, line.Sender, sender.Name, senderIsBot,
                line.Text, line.Type == ChatType.Whisper || Chat.PlayerbotChatPrompts.Addresses(line.Text, player.Name), FirstHearing: false))
            && sender.Guid != master)
            return;
        bool botsHeard = _chat is { IsActive: true } listening && listening.SpeechOptions is { Enabled: true, BotToBot: true };
        switch (PlayerbotChatCommands.Classify(line, player.Guid, master, senderIsBot, botsHeard))
        {
            case PlayerbotChatSource.Master when sender is not null:
            {
                // The master's order counts from the moment the group has a master, before the bot's first budgeted turn: engaging
                // here keeps that turn from resetting the order (Drives engages only a master it is not engaged to yet).
                bool parsed = PlayerbotChatCommands.TryParse(line.Text, out PlayerbotPartyCommand command);
                if (GroupOwns?.Invoke(player, sender, parsed ? command : null) != true && (!_engaged || master != _master)) Engage(player, master);
                _masterLostMs = -1;
                if (parsed) Execute(player, sender, command);
                else if (isGm && PlayerbotChatCommands.IsDebugQuestion(line.Text)) Tell(player, sender.Name, DebugLine(player));
                else if (PlayerbotChatCommands.IsHelpQuestion(line.Text)) Tell(player, sender.Name, PlayerbotChatCommands.HelpFor(isGm));
                else if (Converse(player, line, sender, master)) { }
                else if (line.Type == ChatType.Whisper && _replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.HelpFor(isGm));
                break;
            }
            case PlayerbotChatSource.Stranger when sender is not null:
                if (isGm && PlayerbotChatCommands.TryParse(line.Text, out PlayerbotPartyCommand gmCmd) && PlayerbotChatCommands.IsGmInspectCommand(gmCmd))
                    Execute(player, sender, gmCmd);
                else if (isGm && PlayerbotChatCommands.IsDebugQuestion(line.Text))
                    Tell(player, sender.Name, DebugLine(player));
                else if (PlayerbotChatCommands.IsHelpQuestion(line.Text))
                {
                    if (isGm || _replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.HelpFor(isGm));
                }
                else if (Converse(player, line, sender, master)) { }
                else if (_replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.PoliteReply);
                break;
            case PlayerbotChatSource.Bot when sender is not null:
                // Another bot's line: only the chat service answers it, only when it names this bot (or whispers it), and only while the
                // exchange between the two has turns and budget left (PlayerbotSpeechLimiter.NextBotTurn). Never an order, never a fixed reply.
                // A conversation the director runs between the two writes both sides: neither answers the other on its own then.
                if ((line.Type == ChatType.Whisper || Chat.PlayerbotChatPrompts.Addresses(line.Text, player.Name)) && !_chat!.IsDirected(sender.Guid, player.Guid))
                {
                    int turn = _chat!.NextBotTurn(sender.Guid, player.Guid);
                    if (turn > 0) Converse(player, line, sender, master, senderIsBot: true, turn);
                }

                break;
            case PlayerbotChatSource.Ignore when sender is not null && !senderIsBot && sender.Guid != player.Guid:
                // The master's command word on a channel that is no order (say, yell, guild): dropped, and the master sees nothing.
                if (!master.IsEmpty && sender.Guid == master && PlayerbotChatCommands.TryParse(line.Text, out PlayerbotPartyCommand unheard))
                    PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.CommandIgnored,
                        $"the master's '{unheard}' command came as {line.Type} chat; only whispers and party or raid chat are orders", AnomalySeverity.Info);
                // A line from a real player on any other channel (party, raid, say, yell, battleground, guild, a world channel): answered
                // on that channel when it names the bot, or, in guild chat, when it is a question put to the whole guild.
                if (Chat.PlayerbotChatPrompts.Addresses(line.Text, player.Name))
                {
                    if (isGm && PlayerbotChatCommands.IsDebugQuestion(line.Text)) { Tell(player, sender.Name, DebugLine(player)); break; }
                    if (PlayerbotChatCommands.IsHelpQuestion(line.Text) && (_chat is not { IsActive: true } || !_chat.Answers(line.Type)))
                    {
                        if (isGm || _replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.HelpFor(isGm));
                        break;
                    }
                }
                Converse(player, line, sender, master);
                break;
        }
    }

    // --- chat ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Hand a line to the chat service (docs/areas/playbots.md, Bot chat). False when chat is off or the line is not one the bots answer
    /// (the fixed replies of before then apply); true when the line is the chat's, whether or not a reply comes (a cooldown, a spent
    /// budget or a full queue means silence). The answer goes back on the line's own channel (<see cref="Chat.BotSpeech.ReplyChannelFor"/>:
    /// raid in raid chat, a world channel line in that channel) as a reply to the speaker, through the speech limits. A whisper is always
    /// the bot's; any other line only when it names the bot, or, in guild chat, when a real player puts a question to the whole guild
    /// (one bot answers: the per-speaker cooldown keeps the others quiet). <paramref name="senderIsBot"/>: another bot spoke, at
    /// <paramref name="turn"/> of their exchange.
    /// </summary>
    private bool Converse(Player player, PlayerbotChatLine line, Player sender, ObjectGuid master, bool senderIsBot = false, int turn = 0)
    {
        if (_chat is not { IsActive: true } chat || !chat.Answers(line.Type) || line.Text.Length == 0) return false;
        if (Chat.BotSpeech.ReplyChannelFor(line.Type) is not { } replyOn) return false;
        bool addressed = line.Type == ChatType.Whisper || Chat.PlayerbotChatPrompts.Addresses(line.Text, player.Name);
        if (!addressed && (line.Type != ChatType.Guild || senderIsBot || !Chat.PlayerbotChatPrompts.IsOpenQuestion(line.Text))) return false;
        if (line.Type is ChatType.Say or ChatType.Yell && sender.Team != player.Team) return false;
        if (replyOn == Chat.BotSpeechChannel.World && line.ChannelName is null) return false;

        _chatEvents.Observe(player, Now);
        bool fromMaster = !senderIsBot && !master.IsEmpty && sender.Guid == master;
        bool isGm = !senderIsBot && IsGm(sender);
        var speech = new Chat.BotSpeech(replyOn, string.Empty, senderIsBot ? "bot-reply" : "reply",
            senderIsBot ? Chat.BotSpeechPriority.Normal : Chat.BotSpeechPriority.High)
        {
            Target = sender.Name, TargetGuid = sender.Guid, ChannelName = line.ChannelName, IsReply = true, ToBot = senderIsBot, Turn = turn,
        };
        PlayerbotGroupInvites.Standing? standingOfSender = senderIsBot ? null : InviteStanding?.Invoke(player, sender); // once per line, for the invite and the trade
        var ask = new Chat.BotChatAsk(_botId, player.Guid, Persona(player, master, isGm, sender, line.Text), sender.Guid, sender.Name, line.Type, line.Text, fromMaster,
            !senderIsBot && InviteAllowed(player, sender, standingOfSender), senderIsBot ? 0 : sender.AccountId, FromGm: isGm, Speech: speech, SenderIsBot: senderIsBot,
            Addressed: addressed, TradeAllowed: !senderIsBot && TradeAllowed(player, sender, standingOfSender));
        // The AI disclosure comes first, once per login of the speaker (a new Player is made at every login); never to a bot.
        if (chat.TryAsk(ask) == Chat.BotChatAdmission.Queued && !senderIsBot
            && chat.TakeDisclosure(ask, Chat.PlayerbotChatSafety.LoginOf(sender)) is { } notice)
            Gm.Core.GmReplies.SendSystemMessage(sender, notice);
        return true;
    }

    /// <summary>
    /// A text emote aimed at the bot (SMSG_TEXT_EMOTE naming it): answered in kind (<see cref="BotSocial.PlayerbotSocialEmotes.ReplyTo"/>:
    /// a wave with a wave, a bow with a bow, a hug with a blush) through the speech API, at most once per
    /// <see cref="Chat.PlayerbotSpeechOptions.EmoteReplyGapSeconds"/> per emoter, while alive and out of combat. Another bot's emote is
    /// answered only within the bot-to-bot turns, so two bots never wave at each other forever.
    /// </summary>
    private void OnTextEmote(Player player, byte[] payload)
    {
        if (!PlayerbotChatCommands.TryReadTextEmote(payload, out PlayerbotTextEmote emote) || emote.Emoter == player.Guid) return;
        Player? emoter = _session.World.FindOnlinePlayer(emote.Emoter);
        // As in OnChat: an emote from a player on the bot's ignore list is not noticed (no memory change, no reply); the master and a game master are never dropped.
        if (emote.Emoter != ResolveMaster(player) && !(emoter is not null && IsGm(emoter))
            && Services.Social?.Friends.HasIgnore(player, emote.Emoter) == true)
            return;
        RecordEmote(player, in emote);
        if (_chat is not { IsActive: true } chat || chat.SpeechOptions is not { Enabled: true, EmoteReplies: true } options) return;
        if (!string.Equals(emote.TargetName, player.Name, StringComparison.OrdinalIgnoreCase)) return;
        if (!player.IsAlive || player.Combat.IsInCombat || emoter is null) return;
        bool fromBot = emoter.Session is WorldSession { IsManaged: true };
        long now = Now;
        long gap = options.EmoteReplyGapSeconds * 1000L;
        if (_emoteReplies.Count > 64)
            foreach (KeyValuePair<ObjectGuid, long> stale in _emoteReplies.Where(pair => now - pair.Value >= gap).ToArray()) _emoteReplies.Remove(stale.Key);
        if (_emoteReplies.TryGetValue(emoter.Guid, out long at) && now - at < gap) return;
        int turn = 0;
        if (fromBot && (!options.BotToBot || (turn = chat.NextBotTurn(emoter.Guid, player.Guid)) <= 0)) return;
        if (EmoteAnswer(player, emoter, fromBot, emote.TextEmote, now) is not { } answer) return;
        _emoteReplies[emoter.Guid] = now;
        chat.TrySpeak(_botId, player.Guid, Chat.BotSpeech.EmoteAt(answer, emoter.Guid, "emote-reply") with { IsReply = true, ToBot = fromBot, Turn = turn });
    }

    /// <summary>
    /// The answer to a text emote (docs/areas/playbots-social.md, Rivalry): with <c>Social:Rivalry:Enabled</c> by the bot's stance toward the emoter in the
    /// normal layer of its memory, War Mode or not (an answer is budgeted speech, so a war-driven one would change when the bot may speak to a bot); a
    /// hash roll decides it, and nothing rude is sent to a bot without <c>RudeAtBots</c> or to a player who said "rivals off". Without it, as before.
    /// </summary>
    private uint? EmoteAnswer(Player player, Player emoter, bool fromBot, uint textEmote, long now)
    {
        BotSocial.PlayerbotSocialRivalryOptions rivalry = _options.Social.Rivalry;
        if (!rivalry.Enabled) return BotSocial.PlayerbotSocialEmotes.ReplyTo(textEmote, _random);
        BotSocial.PlayerbotStance stance = BotSocial.PlayerbotStance.Neutral;
        if (rivalry.Emotes.RivalEmotes && Social is { Enabled: true } social && social.Memory.TryGet(_botId, emoter.Guid.Value, out PlayerbotRelation relation))
            stance = BotSocial.PlayerbotRivalryRules.StanceOf(relation, BotSocial.PlayerbotRivalryLayer.Normal, rivalry.Emotes.FriendlyAffinity);
        bool optedOut = !fromBot && Social?.RivalryOptOut?.IsOptedOut(emoter.Guid.Value, emoter.AccountId) == true;
        double roll = BotSocial.PlayerbotRivalryRules.ReplyRoll(player.Guid.Value, emoter.Guid.Value, textEmote, now);
        uint? answer = BotSocial.PlayerbotRivalryRules.ReplyTo(stance, textEmote, fromBot, roll, rivalry.Emotes.RudeAtBots, optedOut);
        // A rude answer to a real player keeps the lane's safeguards: opt-in when the world asks for it, and no capital, safe area or unplaceable zone.
        if (!fromBot && answer is { } rude && BotSocial.PlayerbotEmoteMoods.Classify(rude) == BotSocial.PlayerbotEmoteMood.Rude
            && ((rivalry.Emotes.RudeRequiresOptIn && Social?.RivalryOptOut?.ChoiceOf(emoter.Guid.Value, emoter.AccountId) != true) || RudeReplyIsQuiet(player, emoter, rivalry)))
            answer = BotSocial.PlayerbotSocialEmotes.TextEmotes.Shrug;
        if (answer is { } given)
        {
            if (BotSocial.PlayerbotEmoteMoods.Classify(given) == BotSocial.PlayerbotEmoteMood.Rude) BotSocial.PlayerbotRivalryMeters.CountEmote("reply_rude");
            else if (given == BotSocial.PlayerbotSocialEmotes.TextEmotes.Shrug) BotSocial.PlayerbotRivalryMeters.CountEmote("reply_shrug");
        }

        return answer;
    }

    private bool RudeReplyIsQuiet(Player player, Player emoter, BotSocial.PlayerbotSocialRivalryOptions rivalry)
    {
        ArcaneCore.Game.Maps.Terrain.AreaTable? areas = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(_session.World).Areas;
        IReadOnlySet<uint> safeAreas = rivalry.Safeguards.SafeAreaIds();
        return BotSocial.PlayerbotRivalryRules.IsQuiet(player, areas, safeAreas) || BotSocial.PlayerbotRivalryRules.IsQuiet(emoter, areas, safeAreas);
    }

    /// <summary>
    /// A text emote aimed at this bot by name, from any faction, moves its memory of the emoter: friendly raises affinity, rude lowers it, the
    /// rest is ignored. Independent of the chat service, so a bot with chat off still remembers.
    /// </summary>
    private void RecordEmote(Player player, in PlayerbotTextEmote emote)
    {
        if (Social is not { Enabled: true } social) return;
        if (!string.Equals(emote.TargetName, player.Name, StringComparison.OrdinalIgnoreCase)) return;
        BotSocial.PlayerbotEmoteMood mood = BotSocial.PlayerbotEmoteMoods.Classify(emote.TextEmote);
        if (mood == BotSocial.PlayerbotEmoteMood.Help)
        {
            // A call for help aimed at this bot: the social pass decides whether it answers (BotSocial.PlayerbotHelpLane).
            social.NoteHelpCall(player.Guid, emote.Emoter);
            return;
        }

        if (mood == BotSocial.PlayerbotEmoteMood.Neutral) return;
        BotSocial.PlayerbotSocialEvent noted = mood switch
        {
            BotSocial.PlayerbotEmoteMood.Friendly => BotSocial.PlayerbotSocialEvent.FriendlyEmote,
            BotSocial.PlayerbotEmoteMood.Apology => BotSocial.PlayerbotSocialEvent.Apologized,
            _ => BotSocial.PlayerbotSocialEvent.RudeEmote,
        };
        // The world lookup is part of the recording step: a fault in it is recorded at the social-memory site, never thrown into the packet handler.
        ObjectGuid emoterGuid = emote.Emoter; // an in parameter cannot be captured
        social.Guarded(() =>
        {
            bool otherFaction = _session.World.FindOnlinePlayer(emoterGuid) is { } emoter && emoter.Team != player.Team;
            social.NoteById(_botId, player.Guid, emoterGuid, noted, otherFaction);
        });
    }

    /// <summary>The bot's facts for a line it starts itself (world thread; the social features' guild announcements and greetings).</summary>
    internal Chat.BotChatPersona PersonaFor(Player p) => Persona(p, ResolveMaster(p));

    /// <summary>
    /// The bot's facts for the chat (world thread): who and where it is, what it does, its group and recent events, and its real state
    /// (<see cref="Chat.PlayerbotPersonaFacts"/>). <paramref name="speaker"/> and <paramref name="heard"/>: the player being answered and
    /// the line (whether the speaker is in trade range or the group; a place the line asks about).
    /// </summary>
    private Chat.BotChatPersona Persona(Player player, ObjectGuid master, bool includeDebug = false, Player? speaker = null, string? heard = null)
    {
        (string? zone, string? subzone) = ZoneNames(player);
        PlayerbotGoalKind? goal = null;
        uint questId = 0;
        if (_engaged) goal = Goal;
        else if (BrainGoal is { } brain) (goal, questId) = brain();
        string? quest = questId == 0 ? null
            : _session.Services.GetService<QuestNpcFeature>()?.Services.Quests.Get(questId)?.Title is { Length: > 0 } title ? title : null;
        string? masterName = master.IsEmpty ? null : _session.World.FindOnlinePlayer(master)?.Name;
        bool inGroup = Services.Social?.Groups.GetGroup(player.Guid) is not null;
        string? debugSummary = includeDebug ? DebugLine(player) : null;
        Chat.BotChatFacts facts = Chat.PlayerbotPersonaFacts.Build(_session, player, questId, heard);
        if (speaker is not null)
            facts = facts with
            {
                SpeakerNearby = ReferenceEquals(speaker.Map, player.Map) && Distance(speaker, player) <= TradeReach,
                SpeakerInGroup = Services.Social?.Groups.AreInSameGroup(player.Guid, speaker.Guid) == true,
            };
        return new Chat.BotChatPersona(player.Name, player.Race, player.Class, player.Gender, player.Level, zone, subzone, goal, quest,
            masterName, inGroup, _chatEvents.Recent(Now), DebugSummary: debugSummary, Facts: facts, Relation: RelationWith(player, speaker),
            Mood: Social?.Mood.Get(_botId) ?? 0f);
    }

    /// <summary>
    /// What the bot remembers of <paramref name="speaker"/> for the chat (null when it remembers nothing, memory is off or there is no speaker).
    /// Bot-side data only: bands, a topic key and the bot's own summary.
    /// </summary>
    private Chat.BotChatRelation? RelationWith(Player player, Player? speaker)
    {
        if (speaker is null || Social is not { Enabled: true } social) return null;
        try
        {
            if (!social.Memory.TryGet(_botId, speaker.Guid.Value, out PlayerbotRelation relation)) return null;
            BotSocial.PlayerbotTraits traits = BotSocial.PlayerbotTraits.For(player.Guid.Value);
            Chat.BotChatRole? role = relation.Flags.HasFlag(PlayerbotRelationFlags.PrefersTank) ? Chat.BotChatRole.Tank
                : relation.Flags.HasFlag(PlayerbotRelationFlags.PrefersHealer) ? Chat.BotChatRole.Healer
                : relation.Flags.HasFlag(PlayerbotRelationFlags.PrefersDamage) ? Chat.BotChatRole.Damage : null;
            return new Chat.BotChatRelation(relation.Familiarity, social.Memory.EffectiveAffinity(relation), BotSocial.PlayerbotSocialBands.Of(social.Memory.EffectiveAffinity(relation)),
                social.Memory.IsRival(relation), role,
                relation.LastTopic.Length == 0 ? null : relation.LastTopic, relation.Summary.Length == 0 ? null : relation.Summary,
                BotSocial.PlayerbotTraits.Band(traits.Sociability), BotSocial.PlayerbotTraits.Band(traits.Chattiness));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(BotSocial.PlayerbotSocialRecorder.FaultSite, error);
            return null;
        }
    }

    /// <summary>The trade distance (vmangos TRADE_DISTANCE, 11.11 yards; a little less, so the server never refuses it).</summary>
    internal const float TradeReach = 10f;

    /// <summary>
    /// World thread: a social act a chat reply asks for (<see cref="Chat.BotChatAction"/>), checked again here and carried out only
    /// through the bot's own client packets: an invitation (CMSG_GROUP_INVITE) when the bot has no group or leads one with room, the
    /// speaker is free, of its faction and allowed by its invite policy; a trade (CMSG_INITIATE_TRADE) with a speaker in trade range who is
    /// in its group or allowed by its policy, while the bot is alive and out of combat; a quest share for its party master only; an LFG
    /// line in the LookingForGroup channel when it is free. Never for another bot.
    /// </summary>
    private void PerformAction(Player player, Chat.BotChatReply reply)
    {
        if (reply.Action == Chat.BotChatAction.None || _session.World.FindOnlinePlayer(reply.SenderGuid) is not { } speaker || speaker.Guid == player.Guid) return;
        if (speaker.Session is WorldSession { IsManaged: true }) return;
        SocialContext? social = Services.Social;
        switch (reply.Action)
        {
            case Chat.BotChatAction.Invite:
            {
                if (social is null || speaker.Team != player.Team || !InviteAllowed(player, speaker) || social.Groups.GetGroup(speaker.Guid) is not null) return;
                if (social.Groups.GetGroup(player.Guid) is { } group && (!group.IsLeader(player.Guid) || group.IsFull)) return;
                var writer = new PacketWriter(speaker.Name.Length + 1);
                writer.WriteCString(speaker.Name);
                Act(WorldOpcode.CmsgGroupInvite, writer.ToArray(), budgeted: false);
                return;
            }

            case Chat.BotChatAction.OpenTrade:
            {
                if (!player.IsAlive || player.Combat.IsInCombat || !ReferenceEquals(speaker.Map, player.Map) || Distance(speaker, player) > TradeReach) return;
                bool trusted = social?.Groups.AreInSameGroup(player.Guid, speaker.Guid) == true || TradeAllowed(player, speaker);
                if (trusted)
                {
                    // An agreed deal with the speaker is the desk's: its own Drive initiates the window.
                    if (TradeDesk?.StateOf(_botId, speaker.Guid) is BotSocial.PlayerbotDealState.Agreed) return;
                    BotSocial.PlayerbotTradeOwners? owners = Owners;
                    if (owners?.TryClaim(player, BotSocial.PlayerbotTradeOwner.Gift, speaker.Guid, LiveTrade(player)) == false) return;
                    if (Act(WorldOpcode.CmsgInitiateTrade, PlayerbotNavigation.GuidPayload(speaker.Guid.Value), budgeted: false))
                    {
                        _tradePartner = speaker.Guid; // only a window this bot really asked for
                        _tradeCounts = false;
                        owners?.Of(player, LiveTrade(player)); // bind the claim to the window this initiate just opened, so it can never adopt a later one
                    }
                    else owners?.Release(player, BotSocial.PlayerbotTradeOwner.Gift);
                }
                return;
            }

            case Chat.BotChatAction.ShareQuest:
                if (ResolveMaster(player) == speaker.Guid) ShareQuestsWithParty(player);
                return;

            case Chat.BotChatAction.JoinLfg:
            {
                if (_chat is not { } chat || social?.Groups.GetGroup(player.Guid) is not null || !player.IsAlive) return;
                Chat.BotChatPersona persona = Persona(player, ObjectGuid.Empty);
                if (persona.Facts is null) return;
                if (_options.Chat.Voice.LfgNearDungeonOnly)
                {
                    if (LfgDungeon(player) is not { } lfgDungeon) return;
                    persona = persona with { Facts = persona.Facts with { SuitedDungeon = lfgDungeon.Name } };
                }
                else if (persona.Facts.SuitedDungeon is null)
                {
                    return;
                }

                var ask = new Chat.BotChatAsk(_botId, player.Guid, persona, player.Guid, player.Name, ChatType.Channel, string.Empty, false, false);
                if (Chat.PlayerbotBuiltinChat.Pick("post.lfg", ask, null, _random) is { } text)
                    chat.TrySpeak(_botId, player.Guid, Chat.BotSpeech.InChannel(Chat.PlayerbotChannelMembership.LookingForGroup, text, "lfg", Chat.BotSpeechPriority.Normal));
                return;
            }
        }
    }

    /// <summary>
    /// The dungeon an LFG line of the bot names (<c>JoinLfg</c>, under <c>LfgNearDungeonOnly</c>; without it the line names the persona's own
    /// suited dungeon, unchanged): among the five-player dungeons whose range holds the bot's level (<see cref="Chat.PlayerbotDungeons.SuitedFor"/>'s
    /// range, highest minimum level first) the first whose entrance the bot is near enough to advertise (<c>PlayerbotLfgLocality.ForPlace</c>,
    /// no summon; the entrance on the bot's map nearest to it, else the lowest-id entrance on another map). Null when none, so nothing is posted.
    /// </summary>
    private Chat.PlayerbotDungeon? LfgDungeon(Player player)
    {
        Game.Maps.Templates.WorldMaps maps = Game.Maps.Templates.WorldMaps.Of(_session.World);
        var at = new Vector3(player.X, player.Y, player.Z);
        foreach (Chat.PlayerbotDungeon dungeon in Chat.PlayerbotDungeons.All.Where(d => !d.Raid && d.MapId != 0 && player.Level >= d.MinLevel && player.Level <= d.MaxLevel)
                     .OrderByDescending(d => d.MinLevel))
        {
            ArcaneCore.Kernel.WorldData.AreaTriggerTemplate? entrance = Groups.PlayerbotGroupCoordinator.EntranceTo(maps, player.MapId, dungeon.MapId, at);
            if (entrance is null)
            {
                foreach (ArcaneCore.Kernel.WorldData.AreaTriggerTemplate trigger in maps.AreaTriggers)
                {
                    if (trigger.MapId == dungeon.MapId || maps.FindAreaTriggerTeleport(trigger.Id)?.TargetMap != dungeon.MapId) continue;
                    if (entrance is null || trigger.Id < entrance.Id) entrance = trigger;
                }
            }

            if (entrance is null) continue;
            if (Chat.PlayerbotLfgLocality.ForPlace(_session, player, _options, entrance.MapId, new Vector3(entrance.X, entrance.Y, entrance.Z), false).MayAdvertise) return dungeon;
        }

        return null;
    }

    private (string? Zone, string? Subzone) ZoneNames(Player player)
    {
        try
        {
            if (player.Map is not { } map) return (null, null);
            (uint zoneId, uint areaId) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
            if (zoneId == 0 && player.ZoneId != 0) zoneId = player.ZoneId;
            Game.Maps.Terrain.AreaTable areas = Game.Maps.Templates.WorldMaps.Of(_session.World).Areas;
            string? zone = zoneId == 0 ? null : areas.GetById(zoneId)?.Name;
            string? subzone = areaId == 0 || areaId == zoneId ? null : areas.GetById(areaId)?.Name;
            return (string.IsNullOrWhiteSpace(zone) ? null : zone, string.IsNullOrWhiteSpace(subzone) ? null : subzone);
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException or KeyNotFoundException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("playerbot.party-zone-names", error);
            return (null, null); // a minimal host without map data
        }
    }

    /// <summary>
    /// Whether the bot would accept an invitation from <paramref name="sender"/> (its invite policy). Under <see cref="PlayerbotInvitePolicy.Affinity"/>
    /// this includes a sender on the bot's own quest or dungeon goal: right for a group invitation and the help-quest invite (the same goal is the
    /// reason to group), not for a trade (<see cref="TradeAllowed"/>).
    /// </summary>
    private bool InviteAllowed(Player player, Player sender) => InviteAllowed(player, sender, InviteStanding?.Invoke(player, sender));

    /// <summary><see cref="InviteAllowed(Player, Player)"/> with the sender's standing already worked out (once per line, for the invite and the trade).</summary>
    private bool InviteAllowed(Player player, Player sender, PlayerbotGroupInvites.Standing? standing)
    {
        if (Services.Social is not { } social) return false;
        PlayerbotPartyOptions party = _options.Party;
        return PlayerbotGroupInvites.Allows(party.InvitePolicy, party.Allowlist, sender.Name,
            PlayerbotGroupInvites.SameGuild(social, player, sender), PlayerbotGroupInvites.OnBotsFriendList(social, player, sender), standing);
    }

    /// <summary>
    /// Whether the bot would open a trade with <paramref name="sender"/> (who is not in its group): the invite policy's own grounds (the allowlist, a guild
    /// mate, a friend of the bot's own list, a well-known player under <see cref="PlayerbotInvitePolicy.Affinity"/>, anyone under
    /// <see cref="PlayerbotInvitePolicy.Anyone"/>) and never a rival, but NOT being on the same quest or dungeon goal: that is a reason to group, not to
    /// hand items over.
    /// </summary>
    private bool TradeAllowed(Player player, Player sender) => TradeAllowed(player, sender, InviteStanding?.Invoke(player, sender));

    private bool TradeAllowed(Player player, Player sender, PlayerbotGroupInvites.Standing? invite)
    {
        if (Services.Social is not { } social) return false;
        PlayerbotPartyOptions party = _options.Party;
        PlayerbotGroupInvites.Standing? standing = invite is { } found ? found with { SharesGoal = false } : null;
        if (standing is { Rival: true }) return party.Allowlist.Any(name => string.Equals(name, sender.Name, StringComparison.OrdinalIgnoreCase));
        return PlayerbotGroupInvites.Allows(party.InvitePolicy, party.Allowlist, sender.Name,
            PlayerbotGroupInvites.SameGuild(social, player, sender), PlayerbotGroupInvites.OnBotsFriendList(social, player, sender), standing);
    }

    /// <summary>
    /// World thread: say a chat line (a reply, an announcement or a <see cref="Chat.PlayerbotChat.TrySpeak"/> line) through the bot's own
    /// session (<see cref="Chat.PlayerbotSpeechDelivery"/>: the place checks, the speech limits, then CMSG_MESSAGECHAT or CMSG_TEXT_EMOTE),
    /// and carry out the master's order when the speaker is still the bot's master.
    /// </summary>
    internal void DeliverChat(Player player, Chat.BotChatReply reply)
    {
        if (_chat is { } chat && SpeechOf(reply) is { } speech)
            Chat.PlayerbotSpeechDelivery.Deliver(chat, _botId, _session, player, speech);
        else _chat?.Lost(reply.Speech); // nothing to say (the provider chain chose silence): a line waiting to be said is let go
        if (reply.Action != Chat.BotChatAction.None) PerformAction(player, reply);

        if (reply.Command is { } command)
        {
            ObjectGuid master = ResolveMaster(player);
            if (master.IsEmpty || master != reply.SenderGuid || _session.World.FindOnlinePlayer(master) is not { } current) return;
            if (GroupOwns?.Invoke(player, current, command) != true && (!_engaged || master != _master)) Engage(player, master);
            _masterLostMs = -1;
            Execute(player, current, command, acknowledge: reply.AcknowledgeCommand);
        }
    }

    /// <summary>
    /// The envelope a reply is said with: its own (<see cref="Chat.BotChatReply.Speech"/>), or for the replies of before, the channel the
    /// line came on (a whisper back to the speaker, party, say, a guild announcement in the guild it was written for). Null when there is
    /// nothing to say.
    /// </summary>
    internal static Chat.BotSpeech? SpeechOf(Chat.BotChatReply reply)
    {
        string text = reply.Text ?? string.Empty;
        if (reply.Speech is { } own)
            return own.Channel == Chat.BotSpeechChannel.Emote ? own : text.Length == 0 ? null : own with { Text = text };
        if (text.Length == 0 || Chat.BotSpeech.ReplyChannelFor(reply.Channel) is not { } channel) return null;
        bool announcement = reply.Channel == ChatType.Guild && reply.Guild is not null;
        return new Chat.BotSpeech(channel, text, announcement ? "guild" : "reply", announcement ? Chat.BotSpeechPriority.Normal : Chat.BotSpeechPriority.High)
        {
            Target = reply.SenderName, TargetGuid = announcement ? default : reply.SenderGuid, Guild = reply.Guild, IsReply = !announcement,
        };
    }

    private void OnLootRoll(Player player, byte[] payload)
    {
        if (!PlayerbotLootRolls.TryRead(payload, out PlayerbotLootRolls.StartRoll roll))
        {
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.LootRollUncast,
                string.Create(CultureInfo.InvariantCulture, $"SMSG_LOOT_START_ROLL of {payload.Length} bytes could not be read, so no vote was cast"));
            return;
        }

        RollVote vote = GroupLootVote?.Invoke(player, roll.ItemId)
            ?? PlayerbotLootRolls.VoteFor(player, roll.ItemId, _options.Party.LootRoll, Services.Spells?.System.Store, Services.Spells?.Spellbook);
        if (_myRolls.Count >= 32) _myRolls.Clear();
        _myRolls[(roll.Source.Value, roll.Slot)] = vote;
        if (!Act(WorldOpcode.CmsgLootRoll, PlayerbotLootRolls.Vote(roll, vote), budgeted: false))
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.LootRollUncast,
                string.Create(CultureInfo.InvariantCulture, $"the {vote} vote on item {roll.ItemId} (slot {roll.Slot}) was not dispatched"));
    }

    // The votes this bot cast on the rolls it was offered, until the roll is announced settled (bounded; a lost announcement just ages out).
    private readonly Dictionary<(ulong Source, uint Slot), RollVote> _myRolls = [];

    /// <summary>
    /// A roll was won (SMSG_LOOT_ROLL_WON): when the bot had voted Need and a groupmate won it on a Need vote for an item that groupmate could not use,
    /// the bot remembers it as a ninja looter (<see cref="BotSocial.PlayerbotSocialEvent.NinjaLoot"/>). Costs nothing for a roll the bot did not vote on.
    /// </summary>
    private void OnLootRollWon(Player player, byte[] payload)
    {
        if (!PlayerbotLootRolls.TryReadWon(payload, out PlayerbotLootRolls.RollResult won)) return;
        if (!_myRolls.Remove((won.Source.Value, won.Slot), out RollVote mine) || Social is not { } social || (!social.Enabled && social.Ledger is null)) return; // the ledger is fed with the memory off
        if (won.Winner == player.Guid || _session.World.FindOnlinePlayer(won.Winner) is not { } winner) return;
        // Judging whether the winner could use the item reads the item and spell stores: part of the recording step, so a fault is recorded, not thrown.
        social.Guarded(() =>
        {
            if (!PlayerbotLootRolls.IsNinja(mine, won.Vote, PlayerbotLootRolls.WantsItem(winner, won.ItemId, Services.Spells?.System.Store, Services.Spells?.Spellbook))) return;
            social.NoteById(_botId, player.Guid, won.Winner, BotSocial.PlayerbotSocialEvent.NinjaLoot, winner.Team != player.Team);
        });
    }

    /// <summary>
    /// mangoszero AcceptResurrectAction.h: a resurrection from a member of the bot's group is accepted. With helping on
    /// (<see cref="BotSocial.PlayerbotSocialHelpingOptions.Enabled"/>) a same-faction player the bot does not hold as a rival is accepted too (a stranger who
    /// raises a dead bot); any other offer is declined.
    /// </summary>
    private void OnResurrectRequest(Player player, byte[] payload)
    {
        if (payload.Length < 8) return;
        var caster = new ObjectGuid(BitConverter.ToUInt64(payload, 0));
        bool groupMember = caster != player.Guid && Services.Social?.Groups.AreInSameGroup(player.Guid, caster) == true;
        bool stranger = !groupMember && AcceptsStrangersRez(player, caster);
        bool member = groupMember || stranger;
        var response = new PacketWriter(9);
        response.WriteUInt64(caster.Value);
        response.WriteByte(member ? (byte)1 : (byte)0);
        Act(WorldOpcode.CmsgResurrectResponse, response.ToArray(), budgeted: false);
        if (groupMember) _chat?.Voice.NoteResurrection(_botId, caster, _chat.NowMs); // the bot voice thanks a group member once alive ("ty"); a stranger is thanked by the gratitude lane instead
        // A stranger's resurrection is help: remembered, and thanked by the social pass (BotSocial.PlayerbotGratitudeLane).
        if (stranger && _session.World.FindOnlinePlayer(caster) is { } raiser) Social?.NoteHelp(raiser, player, BotSocial.PlayerbotThanksKind.Resurrect);
    }

    private bool AcceptsStrangersRez(Player player, ObjectGuid caster)
    {
        if (caster == player.Guid || !_options.Social.Helping.Enabled || !_options.Social.Helping.ResurrectStrangers) return false;
        if (_session.World.FindOnlinePlayer(caster) is not { } other || other.Team != player.Team) return false;
        if (Social is not { Enabled: true } social) return true;
        return !social.Memory.TryGet(_botId, caster.Value, out ArcaneCore.Kernel.Characters.PlayerbotRelation relation) || !social.Memory.IsRival(relation);
    }

    /// <summary>
    /// Answer a party member's or master's trade (SMSG_TRADE_STATUS): open the trade window on BeginTrade (1), accept when the
    /// partner accepts on TradeAccept (4) under the rules of <see cref="AnswerPartnerAccept"/>, and run equipment
    /// evaluation when the trade completes so any traded upgrade is worn.
    /// </summary>
    private void OnTradeStatus(Player player, byte[] payload)
    {
        if (payload.Length < 4) return;
        uint status = BitConverter.ToUInt32(payload, 0);
        switch (status)
        {
            case 1 when payload.Length >= 12:
            {
                var trader = new ObjectGuid(BitConverter.ToUInt64(payload, 4));
                if (trader.IsEmpty || trader == player.Guid) return;
                // A trader the trade desk holds a deal with is answered, trusted or not; the window is the desk's (no _tradePartner: the gift rules never touch it).
                BotSocial.PlayerbotTradeOwners? owners = Owners;
                if (TradeDesk?.Claims(_botId, trader, player, LiveTrade(player)) == true)
                {
                    if (!Act(WorldOpcode.CmsgBeginTrade, [], budgeted: false)) owners?.Release(player, BotSocial.PlayerbotTradeOwner.Desk);
                    return;
                }

                ObjectGuid master = ResolveMaster(player);
                bool trusted = trader == _master || trader == master
                    || Services.Social?.Groups.AreInSameGroup(player.Guid, trader) == true;
                if (!trusted) return; // a stranger's request is not answered here, and never takes over the partner an open window is credited to
                if (owners?.TryClaim(player, BotSocial.PlayerbotTradeOwner.Gift, trader, LiveTrade(player)) == false) return; // another driver owns the slot
                _tradePartner = trader;
                _tradeCounts = false;
                if (!Act(WorldOpcode.CmsgBeginTrade, [], budgeted: false)) owners?.Release(player, BotSocial.PlayerbotTradeOwner.Gift);
                else owners?.Of(player, LiveTrade(player));
                break;
            }
            case 2 or 7:
                // The window opened (2) or changed (7, not a terminal status): a desk window reacts at once.
                if (OwnsLiveTrade(player)) DriveDeal(player, TradeDesk!);
                break;
            case 4:
                if (OwnsLiveTrade(player)) DriveDeal(player, TradeDesk!);
                else AnswerPartnerAccept(player);
                UpdateEquipment(player);
                break;
            case 8:
                UpdateEquipment(player);
                // Decide desk ownership before the window is closed out: a desk sale is not a paid tip, so gratitude never sees it.
                BotSocial.PlayerbotDealOutcome deskOutcome = TradeDesk?.WindowClosed(_botId, completed: true) ?? default;
                if (deskOutcome.Kind == BotSocial.PlayerbotDealOutcomeKind.None) Social?.NoteTradeDone(_botId);
                if (!_tradePartner.IsEmpty && _tradeCounts) Social?.NoteById(_botId, player.Guid, _tradePartner, BotSocial.PlayerbotSocialEvent.TradeCompleted,
                    _session.World.FindOnlinePlayer(_tradePartner) is { } partner && partner.Team != player.Team);
                _tradePartner = ObjectGuid.Empty;
                _tradeCounts = false;
                Owners?.ReleaseClosed(player, LiveTrade(player));
                if (deskOutcome.Kind == BotSocial.PlayerbotDealOutcomeKind.Settled) SpendPost(deskOutcome);
                break;
            case 0 or 3 or 5 or 6 or 9 or 10 or 11 or 12 or (>= 14 and <= 22):
                // Every other terminal status ends the window without a completion: forget it, so a later window (the mage fee, a tip) is never credited to it.
                // The status arrives after the economy closed the trade, so a trade that is open now is a newer one (the status is a late cancel of an earlier trade, e.g. the
                // old tip's) and must not close it: the desk is untouched and the registry changes only by its own healing on read. The gift partner and its count are forgotten all the same (a bot-initiated window answered BUSY must
                // not hand its partner to a later one), except when the open trade is this very Gift claim's own bound window.
                if (LiveTrade(player) is { } newer)
                {
                    BotSocial.PlayerbotTradeClaim own = Owners?.Of(player, newer) ?? default; // no registry (the playerbot feature is absent): default, so the partner is forgotten
                    if (!(own.Owner == BotSocial.PlayerbotTradeOwner.Gift && ReferenceEquals(own.Session, newer) && own.Partner == _tradePartner)) { _tradePartner = ObjectGuid.Empty; _tradeCounts = false; }
                    break;
                }

                _tradePartner = ObjectGuid.Empty;
                _tradeCounts = false;
                // CLOSE_WINDOW (12) carries an inventory error and a flag that it is the partner's bags: an error without the flag is the bot's own full bags.
                bool ownBags = status == 12 && payload.Length >= 9 && BitConverter.ToUInt32(payload, 4) != 0 && payload[8] == 0;
                TradeDesk?.WindowClosed(_botId, completed: false, botCaused: ownBags);
                Owners?.ReleaseClosed(player, LiveTrade(player));
                break;
        }
    }

    private TradeSession? LiveTrade(Player player) => _session.Services.GetService<EconomyFeature>()?.TradeOf(player);

    /// <summary>Whether the bot's live trade is a window of the trade desk (an agreed deal with the other side).</summary>
    private bool OwnsLiveTrade(Player player)
        => TradeDesk is { Count: > 0 } desk && LiveTrade(player) is { } trade && desk.Owns(_botId, player, trade);

    /// <summary>
    /// World thread: one step of the trade desk's deal for this bot (<see cref="BotSocial.PlayerbotTradeDesk.Drive"/>), carried out as the bot's own client packet,
    /// never waiting for the action budget: CMSG_INITIATE_TRADE, CMSG_SET_TRADE_ITEM, CMSG_SET_TRADE_GOLD, CMSG_ACCEPT_TRADE or CMSG_CANCEL_TRADE. A line the step
    /// carries is whispered to the partner.
    /// </summary>
    private void DriveDeal(Player player, BotSocial.PlayerbotTradeDesk desk)
    {
        BotSocial.PlayerbotDealStep step = desk.Drive(_botId, player, LiveTrade(player));
        switch (step.Kind)
        {
            case BotSocial.PlayerbotDealStepKind.Initiate:
                if (!Act(WorldOpcode.CmsgInitiateTrade, PlayerbotNavigation.GuidPayload(step.Partner.Value), budgeted: false)) Owners?.Release(player, BotSocial.PlayerbotTradeOwner.Desk);
                else Owners?.Of(player, LiveTrade(player));
                break;
            case BotSocial.PlayerbotDealStepKind.PutItem:
                Act(WorldOpcode.CmsgSetTradeItem, [step.TradeSlot, step.Bag, step.Slot], budgeted: false);
                break;
            case BotSocial.PlayerbotDealStepKind.PutGold:
                Act(WorldOpcode.CmsgSetTradeGold, BitConverter.GetBytes(step.Gold), budgeted: false);
                break;
            case BotSocial.PlayerbotDealStepKind.Accept:
                Act(WorldOpcode.CmsgAcceptTrade, BitConverter.GetBytes(1u), budgeted: false);
                break;
            case BotSocial.PlayerbotDealStepKind.Cancel:
                Act(WorldOpcode.CmsgCancelTrade, [], budgeted: false);
                break;
        }

        if (step.SayKey is { } key && step.PartnerName is { Length: > 0 } name && !step.Partner.IsEmpty) SayDeal(player, key, step.Partner, name);
    }

    /// <summary>One built-in line (<paramref name="key"/>) whispered to the deal's partner; no chat service, no line, or a refusal by the speech limits: silent.</summary>
    private void SayDeal(Player player, string key, ObjectGuid partner, string partnerName)
    {
        if (_chat is not { } chat) return;
        var ask = new Chat.BotChatAsk(_botId, player.Guid, Persona(player, ResolveMaster(player)), partner, partnerName, ChatType.Whisper, string.Empty, false, false);
        if (Chat.PlayerbotBuiltinChat.Pick(key, ask, null, _random) is { } text)
            chat.TrySpeak(_botId, player.Guid, Chat.BotSpeech.WhisperTo(partnerName, partner, text, "deal", Chat.BotSpeechPriority.High));
    }

    /// <summary>A settled desk deal spends the bot's board call for the same item (the WTS or WTB it made), so nobody is quoted a deal that is done.</summary>
    private void SpendPost(BotSocial.PlayerbotDealOutcome settled)
    {
        if (_chat is { } chat && chat.Voice.Find(_botId, chat.NowMs, long.MaxValue) is { } post && post.ItemEntry == settled.Entry) chat.Voice.Forget(_botId);
    }

    /// <summary>
    /// The vendor value (copper, summed over the stack) from which a gift counts as a trade for the bot's memory. P4 S1 turns it into
    /// <c>GiftMinValueCopper</c>; until then it is that option's default.
    /// </summary>
    private const long GiftMinValueCopper = 100;

    /// <summary>
    /// The partner accepted (status 4). The bot accepts only a window it opened or answered for <see cref="_tradePartner"/> (trusted, or the
    /// bot-initiated "trade me" partner) in which its own side is empty, read from the live window in this same world-thread step, so a
    /// partner cannot talk it into giving something it did not check. Notes whether the window counts as a trade for the memory (it received
    /// at least <see cref="GiftMinValueCopper"/> of item vendor value, or the bot gave something). Gold the partner puts in is not counted
    /// (the design's "vendor value"). A window the partner has since changed (its accept cleared) is left alone.
    /// </summary>
    private void AnswerPartnerAccept(Player player)
    {
        if (_tradePartner.IsEmpty || _session.Services.GetService<EconomyFeature>()?.TradeOf(player) is not { Opened: true, Settling: false } trade) return;
        TradeSide mine = trade.SideOf(player), theirs = trade.OtherSide(player);
        if (theirs.Player.Guid != _tradePartner || !theirs.Accepted) return;
        if (Owners?.Is(player, BotSocial.PlayerbotTradeOwner.Gift, _tradePartner, trade) == false) return; // another driver's window (a tip, a fee, a desk deal)
        bool gave = mine.Gold != 0;
        foreach (ObjectGuid _ in mine.TradedItems) { gave = true; break; }
        long received = 0;
        foreach (ObjectGuid guid in theirs.TradedItems)
            if (theirs.Player.Inventory.GetItemByGuid(guid) is { } item) received += (long)item.Template.SellPrice * item.Count;
        _tradeCounts = gave || received >= GiftMinValueCopper;
        if (!gave) Act(WorldOpcode.CmsgAcceptTrade, BitConverter.GetBytes(1u), budgeted: false);
    }

    private void Execute(Player player, Player master, PlayerbotPartyCommand command, bool acknowledge = true)
    {
        switch (command)
        {
            case PlayerbotPartyCommand.Follow:
                _mode = PlayerbotPartyMode.Follow;
                _comeRequested = false;
                _stayPoint = null;
                if (acknowledge) Tell(player, master.Name, "Following.");
                break;
            case PlayerbotPartyCommand.Stay:
                _mode = PlayerbotPartyMode.Stay;
                _comeRequested = false;
                _stayPoint = null; // taken where the bot comes to a stop
                _ordered = ObjectGuid.Empty;
                _route = null;
                _follower.Reset(player);
                PlayerbotMovementControl.Stop(_session, player);
                if (acknowledge) Tell(player, master.Name, "Staying here.");
                break;
            case PlayerbotPartyCommand.Attack:
                // The master's selection may be a creature or a player; a player is ordered on only when the combat hooks allow it (a
                // PvP-flagged enemy, a duel opponent): the order grants the engagement, not the hostility (PlayerbotHostility).
                if (player.Map?.FindObject(master.Selection) is Unit target && target is Creature or Player && IsValidTarget(player, target))
                {
                    _ordered = target.Guid;
                    if (_mode != PlayerbotPartyMode.Follow) _mode = PlayerbotPartyMode.Follow;
                    _comeRequested = false;
                    if (acknowledge) Tell(player, master.Name, "Attacking " + (target is Creature creature ? creature.Template.Name : ((Player)target).Name) + ".");
                }
                else
                {
                    Tell(player, master.Name, "I have no target to attack.");
                }

                break;
            case PlayerbotPartyCommand.Stop:
                _mode = PlayerbotPartyMode.Passive;
                _ordered = ObjectGuid.Empty;
                StopAttacking(player);
                if (acknowledge) Tell(player, master.Name, "Passive: I will not attack.");
                break;
            case PlayerbotPartyCommand.Come:
                _mode = PlayerbotPartyMode.Follow;
                _comeRequested = true;
                _stayPoint = null;
                if (acknowledge) Tell(player, master.Name, "Coming.");
                break;
            case PlayerbotPartyCommand.Status:
                Tell(player, master.Name, StatusLine(player));
                break;
            case PlayerbotPartyCommand.Gear:
                Tell(player, master.Name, GearLine(player));
                break;
            case PlayerbotPartyCommand.Bags:
                Tell(player, master.Name, BagsLine(player));
                break;
            case PlayerbotPartyCommand.Gold:
                Tell(player, master.Name, GoldLine(player));
                break;
            case PlayerbotPartyCommand.Quests:
                Tell(player, master.Name, QuestsLine(player));
                break;
            case PlayerbotPartyCommand.Skills:
                Tell(player, master.Name, SkillsLine(player));
                break;
            case PlayerbotPartyCommand.Talents:
                Tell(player, master.Name, TalentsLine(player));
                break;
            case PlayerbotPartyCommand.Upkeep:
                Tell(player, master.Name, RunUpkeep(player));
                break;
            case PlayerbotPartyCommand.ShareQuests:
                Tell(player, master.Name, ShareQuestsWithParty(player));
                break;
            case PlayerbotPartyCommand.Help:
                Tell(player, master.Name, PlayerbotChatCommands.HelpFor(IsGm(master)));
                break;
            case PlayerbotPartyCommand.Debug:
                Tell(player, master.Name, IsGm(master) ? DebugLine(player) : StatusLine(player));
                break;
            case PlayerbotPartyCommand.Leave:
                if (!_leash.IsEmpty)
                {
                    Tell(player, master.Name, "Going my own way.");
                    Release(player); // a leashed bot has no group to leave: the brain takes over at its next think
                    break;
                }

                Tell(player, master.Name, "Leaving the group.");
                Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
                break;
        }
    }

    /// <summary>This AI's own risk (inspection, tests): it keeps no hazards of creatures beyond a solo bot's strength.</summary>
    internal PlayerbotRisk Risk => _risk;

    /// <summary>The retreat (inspection and tests): a party bot retreats only from a wiping group.</summary>
    internal PlayerbotRetreat Retreat => _risk.Retreat;

    /// <summary>
    /// The risk line of a party bot: it follows its master's lead (<c>decision=follow-master</c>) and never weighs pulls or fights
    /// on its own; with <see cref="PlayerbotRiskOptions.PartyRetreatOnWipe"/> it retreats from a wiping group.
    /// </summary>
    internal string RiskReport => !_options.Risk.Enabled ? "decision=off"
        : _risk.Retreat.Active ? _risk.Report
        : _wipe is { } wipe ? $"decision=follow-master last-retreat={wipe}" : "decision=follow-master";

    /// <summary>
    /// The group is wiping: the master is dead, or half the group or more (the bot counts, alive). Only the members online on the
    /// bot's map are counted.
    /// </summary>
    internal static bool IsWiping(bool masterDead, int membersAlive, int membersDead) => masterDead || membersDead * 2 >= membersAlive + membersDead;

    private bool GroupWiping(Player player, Player? master)
    {
        if (master is null || !ReferenceEquals(master.Map, player.Map)) return false;
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) return false;
        int alive = 0, dead = 0;
        foreach (GroupMemberSlot slot in group.Members)
        {
            Player? member = slot.Guid == player.Guid ? player : _session.World.FindOnlinePlayer(slot.Guid);
            if (member is null || !ReferenceEquals(member.Map, player.Map)) continue;
            if (member.IsAlive) alive++;
            else dead++;
        }

        return IsWiping(!master.IsAlive, alive, dead);
    }

    /// <summary>The 'status' answer: level, health %, mana % (or "no mana") and what the bot is doing.</summary>
    internal string StatusLine(Player player)
    {
        uint health = player.MaxHealth == 0 ? 0 : (uint)((ulong)player.Health * 100 / player.MaxHealth);
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        string mana = player.PowerType == PowerType.Mana && maxMana > 0
            ? (SpellSystem.GetPower(player, PowerType.Mana) * 100UL / maxMana).ToString(CultureInfo.InvariantCulture) + "%"
            : "none";
        return string.Create(CultureInfo.InvariantCulture, $"Level {player.Level}, health {health}%, mana {mana}, {Activity(player)}.");
    }

    /// <summary>The GM 'debug' / 'diag' answer: internal brain/party driver, goal, motion, position, risk, and trace state.</summary>
    internal string DebugLine(Player player)
    {
        string partyState = _engaged
            ? string.Create(CultureInfo.InvariantCulture, $"party(mode={_mode},master={(_masterName.Length > 0 ? _masterName : "none")},leash={!_leash.IsEmpty},goal={Goal}/{TargetEntry})")
            : "autonomous";
        if (BrainDiagnostics?.Invoke(player) is { Length: > 0 } brainDiag)
        {
            string combined = $"Debug: driver={partyState} {brainDiag}";
            return Chat.PlayerbotChatPrompts.Sanitize(combined) ?? combined[..Math.Min(combined.Length, Chat.PlayerbotChatPrompts.MaxReplyLength)];
        }

        (PlayerbotGoalKind? brainGoal, uint questId) = BrainGoal is { } bg ? bg() : (_engaged ? (PlayerbotGoalKind?)Goal : null, 0u);
        uint mapId = player.Map?.MapId ?? player.MapId;
        bool moving = (player.Movement.Flags & MovementFlags.MaskMoving) != 0 || PlayerbotMotion.IsActive(player);
        string fallback = string.Create(CultureInfo.InvariantCulture,
            $"Debug: bot={(_botId == Guid.Empty ? player.Guid.Low.ToString(CultureInfo.InvariantCulture) : _botId.ToString("N")[..8])} driver={partyState} goal={brainGoal?.ToString() ?? "None"} target={TargetEntry} quest={questId} pos=({player.X:F1},{player.Y:F1},{player.Z:F1}) map={mapId} zone={player.ZoneId} hp={player.Health}/{player.MaxHealth} moving={moving} risk={RiskReport}");
        return Chat.PlayerbotChatPrompts.Sanitize(fallback) ?? fallback[..Math.Min(fallback.Length, Chat.PlayerbotChatPrompts.MaxReplyLength)];
    }

    /// <summary>The 'gear' answer: equipped items across slots 0..18, or "Gear: none." when unequipped.</summary>
    internal static string GearLine(Player player)
    {
        var items = new List<string>();
        foreach ((byte slot, Item item) in player.Inventory.Equipped)
        {
            string name = !string.IsNullOrWhiteSpace(item.Template.Name)
                ? item.Template.Name
                : "#" + item.Entry.ToString(CultureInfo.InvariantCulture);
            items.Add($"{EquipmentSlotName(slot)}: {name}");
        }

        return items.Count == 0 ? "Gear: none." : $"Gear: {string.Join(", ", items)}.";
    }

    private static string EquipmentSlotName(byte slot) => slot switch
    {
        InventorySlots.Head => "Head",
        InventorySlots.Neck => "Neck",
        InventorySlots.Shoulders => "Shoulders",
        InventorySlots.Body => "Shirt",
        InventorySlots.Chest => "Chest",
        InventorySlots.Waist => "Waist",
        InventorySlots.Legs => "Legs",
        InventorySlots.Feet => "Feet",
        InventorySlots.Wrists => "Wrists",
        InventorySlots.Hands => "Hands",
        InventorySlots.Finger1 => "Finger1",
        InventorySlots.Finger2 => "Finger2",
        InventorySlots.Trinket1 => "Trinket1",
        InventorySlots.Trinket2 => "Trinket2",
        InventorySlots.Back => "Back",
        InventorySlots.MainHand => "MainHand",
        InventorySlots.OffHand => "OffHand",
        InventorySlots.Ranged => "Ranged",
        InventorySlots.Tabard => "Tabard",
        _ => slot.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The 'bags' answer: free and total slots across the 16-slot backpack and equipped containers.</summary>
    internal static string BagsLine(Player player)
    {
        int free = 0, total = 0;
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            total++;
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is null) free++;
        }

        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, bagSlot) is Container container)
            {
                total += container.Size;
                for (byte s = 0; s < container.Size; s++)
                    if (container[s] is null) free++;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"Bags: {free}/{total} free.");
    }

    /// <summary>The 'gold' answer: current money formatted in gold, silver and copper.</summary>
    internal static string GoldLine(Player player)
    {
        uint money = player.Money;
        uint g = money / 10_000;
        uint s = (money / 100) % 100;
        uint c = money % 100;
        return string.Create(CultureInfo.InvariantCulture, $"Gold: {g}g {s}s {c}c.");
    }

    /// <summary>The 'quests' answer: active quests in the bot's log with title (or quest ID) and completion state.</summary>
    internal string QuestsLine(Player player)
    {
        QuestNpcServices? quests = Services.Quests;
        PlayerNpcState? state = quests?.StateOf(player);
        QuestStore? store = quests?.Quests ?? _session.Services.GetService<QuestStore>();
        var entries = new List<string>();
        var seen = new HashSet<uint>();

        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint questId = player.GetUInt32(UpdateFields.PlayerQuestLog11 + (slot * QuestConstants.FieldsPerSlot));
            if (questId == 0 || !seen.Add(questId)) continue;
            byte slotFlags = player.GetByte(UpdateFields.PlayerQuestLog11 + (slot * QuestConstants.FieldsPerSlot) + 1, 3);
            QuestStatus status = state?.Quests.GetStatus(questId) ?? QuestStatus.None;
            bool complete = status == QuestStatus.Complete || (slotFlags & QuestConstants.SlotStateComplete) != 0;
            bool failed = status == QuestStatus.Failed || (slotFlags & QuestConstants.SlotStateFail) != 0;
            string statusWord = complete ? "complete" : failed ? "failed" : "incomplete";
            string title = store?.Get(questId)?.Title is { Length: > 0 } t
                ? t
                : "#" + questId.ToString(CultureInfo.InvariantCulture);
            entries.Add($"{title} ({statusWord})");
        }

        if (state is not null)
        {
            foreach ((uint questId, QuestStatusData data) in state.Quests.Statuses.OrderBy(r => r.Key))
            {
                if (data.Rewarded || data.Status is not (QuestStatus.Incomplete or QuestStatus.Complete or QuestStatus.Failed))
                    continue;
                if (!seen.Add(questId)) continue;
                string statusWord = data.Status == QuestStatus.Complete ? "complete"
                    : data.Status == QuestStatus.Failed ? "failed" : "incomplete";
                string title = store?.Get(questId)?.Title is { Length: > 0 } t
                    ? t
                    : "#" + questId.ToString(CultureInfo.InvariantCulture);
                entries.Add($"{title} ({statusWord})");
            }
        }

        return entries.Count == 0 ? "Quests: none." : $"Quests: {string.Join(", ", entries)}.";
    }

    /// <summary>The 'skills' answer: known primary/secondary professions (or weapon/class skills when no professions are learned).</summary>
    internal static string SkillsLine(Player player)
    {
        var professions = new List<string>();
        var fallback = new List<string>();
        var seen = new HashSet<uint>();
        if (player.Skills is { } skills)
        {
            foreach ((uint skillId, _) in skills.Known())
            {
                if (!seen.Add(skillId)) continue;
                ushort cur = skills.GetValueBase(skillId);
                ushort max = skills.GetMax(skillId);
                if (cur == 0 && max == 0) continue;
                SkillLineRecord? line = skills.Catalog.Line(skillId);
                string name = line?.Name is { Length: > 0 } n ? n : SkillDisplayName(skillId);
                string formatted = string.Create(CultureInfo.InvariantCulture, $"{name} {cur}/{max}");
                int category = line?.Category ?? InferSkillCategory(skillId);
                if (category is SkillCategories.Profession or SkillCategories.Secondary)
                    professions.Add(formatted);
                else if (category is SkillCategories.Weapon or SkillCategories.Class)
                    fallback.Add(formatted);
            }
        }

        for (int i = 0; i < PlayerSkills.MaxSkills; i++)
        {
            uint word0 = player.GetUInt32(UpdateFields.PlayerSkillInfo11 + (i * 3));
            uint skillId = word0 & 0xFFFFu;
            if (skillId == 0 || !seen.Add(skillId)) continue;
            uint word1 = player.GetUInt32(UpdateFields.PlayerSkillInfo11 + (i * 3) + 1);
            ushort cur = (ushort)(word1 & 0xFFFFu);
            ushort max = (ushort)(word1 >> 16);
            if (cur == 0 && max == 0) continue;
            SkillLineRecord? line = player.Skills?.Catalog.Line(skillId);
            string name = line?.Name is { Length: > 0 } n ? n : SkillDisplayName(skillId);
            string formatted = string.Create(CultureInfo.InvariantCulture, $"{name} {cur}/{max}");
            int category = line?.Category ?? InferSkillCategory(skillId);
            if (category is SkillCategories.Profession or SkillCategories.Secondary)
                professions.Add(formatted);
            else if (category is SkillCategories.Weapon or SkillCategories.Class)
                fallback.Add(formatted);
        }

        List<string> chosen = professions.Count > 0 ? professions : fallback;
        return chosen.Count == 0 ? "Skills: none." : $"Skills: {string.Join(", ", chosen)}.";
    }

    private static int InferSkillCategory(uint skillId) => skillId switch
    {
        SkillIds.Blacksmithing or SkillIds.Leatherworking or SkillIds.Alchemy or SkillIds.Herbalism
            or SkillIds.Mining or SkillIds.Tailoring or SkillIds.Engineering or SkillIds.Enchanting
            or SkillIds.Skinning => SkillCategories.Profession,
        SkillIds.FirstAid or SkillIds.Cooking or SkillIds.Fishing or SkillIds.Poisons
            or SkillIds.Lockpicking or SkillIds.Riding => SkillCategories.Secondary,
        SkillIds.Swords or SkillIds.Axes or SkillIds.Bows or SkillIds.Guns or SkillIds.Maces
            or SkillIds.TwoHandedSwords or SkillIds.Defense or SkillIds.DualWield or SkillIds.Staves
            or SkillIds.TwoHandedMaces or SkillIds.Unarmed or SkillIds.TwoHandedAxes or SkillIds.Daggers
            or SkillIds.Thrown or SkillIds.Crossbows or SkillIds.Wands or SkillIds.Polearms
            or SkillIds.FistWeapons => SkillCategories.Weapon,
        _ => SkillCategories.Generic,
    };

    private static string SkillDisplayName(uint skillId) => skillId switch
    {
        SkillIds.Blacksmithing => "Blacksmithing",
        SkillIds.Leatherworking => "Leatherworking",
        SkillIds.Alchemy => "Alchemy",
        SkillIds.Herbalism => "Herbalism",
        SkillIds.Mining => "Mining",
        SkillIds.Tailoring => "Tailoring",
        SkillIds.Engineering => "Engineering",
        SkillIds.Enchanting => "Enchanting",
        SkillIds.Skinning => "Skinning",
        SkillIds.FirstAid => "First Aid",
        SkillIds.Cooking => "Cooking",
        SkillIds.Fishing => "Fishing",
        SkillIds.Poisons => "Poisons",
        SkillIds.Lockpicking => "Lockpicking",
        SkillIds.Riding => "Riding",
        SkillIds.Swords => "Swords",
        SkillIds.Axes => "Axes",
        SkillIds.Bows => "Bows",
        SkillIds.Guns => "Guns",
        SkillIds.Maces => "Maces",
        SkillIds.TwoHandedSwords => "Two-Handed Swords",
        SkillIds.Defense => "Defense",
        SkillIds.DualWield => "Dual Wield",
        SkillIds.Staves => "Staves",
        SkillIds.TwoHandedMaces => "Two-Handed Maces",
        SkillIds.Unarmed => "Unarmed",
        SkillIds.TwoHandedAxes => "Two-Handed Axes",
        SkillIds.Daggers => "Daggers",
        SkillIds.Thrown => "Thrown",
        SkillIds.Crossbows => "Crossbows",
        SkillIds.Wands => "Wands",
        SkillIds.Polearms => "Polearms",
        SkillIds.FistWeapons => "Fist Weapons",
        _ => "#" + skillId.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The 'talents' answer: active talent build name, spent points and free talent points.</summary>
    internal string TalentsLine(Player player)
    {
        string buildName = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Name;
        TalentService? talents = _session.Services.GetService<TalentFeature>()?.Service;
        uint free = talents?.FreePoints(player) ?? player.GetUInt32(UpdateFields.PlayerCharacterPoints1);
        uint spent = talents?.UsedPoints(player) ?? 0;
        return string.Create(CultureInfo.InvariantCulture, $"Talents: {buildName} ({spent} spent, {free} free).");
    }

    /// <summary>Run out-of-combat buffing and equipment/talent upkeep at once ('buff' / 'upkeep').</summary>
    internal string RunUpkeep(Player player)
    {
        ManagedActionBudget? previous = _session.ManagedBudget;
        if (previous is null || previous.Remaining < 2) _session.ManagedBudget = new ManagedActionBudget(2);
        try
        {
            bool cast = _combatSpells.UpdateOutOfCombat(player, 1000);
            _equipment.Update(player);
            return cast ? "Buffing." : "Upkeep done.";
        }
        finally
        {
            if (previous is null || previous.Remaining < 2) _session.ManagedBudget = previous;
        }
    }

    /// <summary>Share all active quests in the bot's quest log with the party ('share' / 'sharequests').</summary>
    internal string ShareQuestsWithParty(Player player)
    {
        QuestNpcServices? quests = Services.Quests;
        PlayerNpcState? state = quests?.StateOf(player);
        var activeIds = new List<uint>();
        var seen = new HashSet<uint>();

        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint questId = player.GetUInt32(UpdateFields.PlayerQuestLog11 + (slot * QuestConstants.FieldsPerSlot));
            if (questId != 0 && seen.Add(questId)) activeIds.Add(questId);
        }

        if (state is not null)
        {
            foreach ((uint questId, QuestStatusData data) in state.Quests.Statuses.OrderBy(r => r.Key))
            {
                if (!data.Rewarded && data.Status is QuestStatus.Incomplete or QuestStatus.Complete && seen.Add(questId))
                    activeIds.Add(questId);
            }
        }

        if (activeIds.Count == 0) return "No quests to share.";
        foreach (uint questId in activeIds)
            Act(WorldOpcode.CmsgPushquesttoparty, BitConverter.GetBytes(questId), budgeted: false);
        return string.Create(CultureInfo.InvariantCulture, $"Shared {activeIds.Count} quest(s).");
    }

    private void UpdateEquipment(Player player)
    {
        ManagedActionBudget? previous = _session.ManagedBudget;
        if (previous is null) _session.ManagedBudget = new ManagedActionBudget(1);
        try
        {
            _equipment.Update(player);
        }
        finally
        {
            if (previous is null) _session.ManagedBudget = null;
        }
    }

    /// <summary>What the bot is doing, in the words of its status answer.</summary>
    internal string Activity(Player player)
    {
        if (!player.IsAlive) return "dead";
        if (_restUntilMs != 0) return "resting";
        if (_target is { IsAlive: true } target) return "fighting " + (target is Creature creature ? creature.Template.Name : (target as Player)?.Name);
        if (!_engaged || string.IsNullOrEmpty(_masterName)) return "autonomous";
        return _mode switch
        {
            PlayerbotPartyMode.Stay => "staying",
            PlayerbotPartyMode.Passive => "passive, following " + _masterName,
            _ when _engaged && _masterLostMs >= 0 => "waiting for " + _masterName,
            _ => "following " + _masterName,
        };
    }

    // --- master -----------------------------------------------------------------------------------------------------

    /// <summary>The bot's master now (<see cref="PlayerbotParty.ResolveMaster"/>), or empty when it has none.</summary>
    internal ObjectGuid ResolveMaster(Player player) => ResolveMaster(player, out _);

    /// <summary><see cref="ResolveMaster(Player)"/>, saying whether the bot is in a group (false when leashed: no group was looked up).</summary>
    private ObjectGuid ResolveMaster(Player player, out bool grouped)
    {
        grouped = false;
        if (!_leash.IsEmpty) return IsRealPlayerOnline(_leash) ? _leash : ObjectGuid.Empty;
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) return ObjectGuid.Empty;
        grouped = true;
        ObjectGuid[] members = new ObjectGuid[group.Members.Count];
        for (int i = 0; i < members.Length; i++) members[i] = group.Members[i].Guid;
        return PlayerbotParty.ResolveMaster(group.LeaderGuid, members, player.Guid, IsRealPlayerOnline);
    }

    private bool IsRealPlayerOnline(ObjectGuid guid)
        => _session.World.FindOnlinePlayer(guid) is { Session: WorldSession { IsManaged: false } };

    /// <summary>
    /// Whether this AI drives the bot this tick (instead of the brain): it has a master, or its master went offline or left less
    /// than <see cref="PlayerbotPartyOptions.MasterTimeoutSeconds"/> ago (vmangos requestRemoval). When that wait runs out the bot
    /// leaves the group (CMSG_GROUP_DISBAND); out of a group the brain takes over at once.
    /// </summary>
    internal bool Drives(Player player)
    {
        ObjectGuid master = ResolveMaster(player, out bool grouped);
        if (!master.IsEmpty)
        {
            if (!_engaged || master != _master) Engage(player, master);
            _masterLostMs = -1;
            _orphanEpisode.Reset();
            return true;
        }

        if (!_engaged)
        {
            ObserveOrphan(player, grouped);
            return false;
        }

        _orphanEpisode.Reset();
        if (Services.Social?.Groups.GetGroup(player.Guid) is null)
        {
            Release(player);
            return false;
        }

        long now = Now;
        if (_masterLostMs < 0) _masterLostMs = now;
        if (now - _masterLostMs < _options.Party.MasterTimeoutSeconds * 1000L) return true;
        Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
        // Outcome: the bot that waited out its master's absence has left the group, so the brain (and the group matching) can have it again.
        if (Services.Social?.Groups.GetGroup(player.Guid) is not null)
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.LeaderGoneStillGrouped, string.Create(CultureInfo.InvariantCulture,
                $"master {_masterName} has been gone {PlayerbotPartyAnomalies.Seconds(now - _masterLostMs)}s and CMSG_GROUP_DISBAND did not take the bot out of its group"));
        Release(player);
        return false;
    }

    /// <summary>
    /// A bot in a group whose members are all bots or offline, that no coordinator drives (this is only asked for bots the brain drives), for
    /// <see cref="PlayerbotPartyAnomalies.OrphanBudgetMs"/>: nothing will ever take it out of the group (<c>party/leader-gone-still-grouped</c>).
    /// </summary>
    private void ObserveOrphan(Player player, bool grouped)
    {
        Group? group = grouped ? Services.Social?.Groups.GetGroup(player.Guid) : null;
        if (group is { IsBattlegroundGroup: false } && player.IsAlive && !player.Combat.IsInCombat
            && group.Members.Any(m => _session.World.FindOnlinePlayer(m.Guid) is null)
            && _orphanEpisode.ElapsedMs(Now) >= 60_000)
        {
            Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
            if (Services.Social?.Groups.GetGroup(player.Guid) is null)
            {
                _orphanEpisode.Reset();
                return;
            }
        }

        if (!_orphanEpisode.Observe(grouped, Now, PlayerbotPartyAnomalies.OrphanBudgetMs, PlayerbotPartyAnomalies.MaxGapMs)) return;
        PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.LeaderGoneStillGrouped, string.Create(CultureInfo.InvariantCulture,
            $"in a group of {group?.MemberCount ?? 0} with no real player online and no coordinator driving the bot for {PlayerbotPartyAnomalies.Seconds(_orphanEpisode.ElapsedMs(Now))}s"));
        if (group is { IsBattlegroundGroup: false } && player.IsAlive && !player.Combat.IsInCombat)
            Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
    }

    private void Engage(Player player, ObjectGuid master)
    {
        _engaged = true;
        _master = master;
        _masterName = _session.World.FindOnlinePlayer(master)?.Name ?? string.Empty;
        _mode = PlayerbotPartyMode.Follow;
        _comeRequested = false;
        _stayPoint = null;
        _ordered = ObjectGuid.Empty;
        _route = null;
        _follower.Reset(player);
        _target = null;
        _attacking = false;
        _thinkElapsed = 0;
        _releaseTries.Clear();
        _unreachable.Clear();
        _followAngle = (float)(_random.NextDouble() * MathF.Tau);
        _followDistance = PlayerbotParty.MinFollowDistance
            + ((float)_random.NextDouble() * (PlayerbotParty.MaxFollowDistance - 0.5f - PlayerbotParty.MinFollowDistance));
        Goal = PlayerbotGoalKind.Follow;
        TargetEntry = 0;
        _followDrop.NoteFollow(Now);
        // Whatever route the brain was on is not the master's.
        PlayerbotMovementControl.Stop(_session, player);
    }

    /// <summary>
    /// Stop driving the bot now (a controller takes it over): the party state is dropped, so nothing reports a party goal, master or
    /// mode while the controller drives; when the controller lets go, <see cref="Drives"/> engages afresh if the bot is still grouped.
    /// </summary>
    internal void Disengage(Player player)
    {
        if (_engaged) Release(player);
    }

    private void Release(Player player)
    {
        _follower.Reset(player); // first: a fall under way lands before anything stops the bot
        _leash = ObjectGuid.Empty;
        if (_attacking || player.Combat.Victim is not null) StopAttacking(player);
        PlayerbotMovementControl.Stop(_session, player);
        _engaged = false;
        _master = ObjectGuid.Empty;
        _masterName = string.Empty;
        _masterLostMs = -1;
        _mode = PlayerbotPartyMode.Follow;
        _comeRequested = false;
        _stayPoint = null;
        _ordered = ObjectGuid.Empty;
        _route = null;
        _target = null;
        _attacking = false;
        _restUntilMs = 0;
        _rangedIdleMs = 0;
        _releaseTries.Clear();
        _unreachable.Clear();
        _deadSinceMs = -1;
        _corpseRun = false;
        _deadEpisode.Reset();
        _followDrop.Reset();
        _recovery.Reset();
        _combatSpells.Reset();
        _kite.Reset(_session.Player);
        TargetEntry = 0;
    }

    // --- update ------------------------------------------------------------------------------------------------------

    /// <summary>One tick while engaged (world thread, inside the shared action budget like the brain).</summary>
    internal void Update(Player player, uint elapsedMs)
    {
        if (!_engaged) return;
        // A client acknowledges the server's teleports and movement orders before anything else; a far teleport leaves the bot in
        // no map until its MSG_MOVE_WORLDPORT_ACK, so this comes before the in-world check.
        if (!player.IsInWorld)
        {
            PlayerbotMovementControl.Update(_session, player);
            return;
        }

        bool dead = !player.IsAlive;
        if (dead && !_deathRetired)
        {
            _risk.OnDeath(player);
            _follower.Reset(player); // a fall under way is forgotten, not landed: the corpse lies where the bot died
            RetireDeath();
            _deathRetired = true;
        }

        if (PlayerbotMovementControl.Update(_session, player)) return;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;

        if (PlayerbotMotion.ConsumeLoop(player))
        {
            _route = null;
            _follower.Reset(player);
        }
        if (dead)
        {
            UpdateDead(player, interval);
            return;
        }

        _deathRetired = false;
        _deadSinceMs = -1;
        _corpseRun = false;
        _deadEpisode.Reset();
        _recovery.Reset();

        // In the air of a drop to the master: nothing else until it lands (a client cannot act mid-fall either).
        if (_follower.Airborne)
        {
            _follower.ContinueFall(player);
            Goal = PlayerbotGoalKind.Follow;
            return;
        }

        Player? master = _session.World.FindOnlinePlayer(_master);
        if (master is not null && _followDrop.Observe(_mode, player.Map is not null && ReferenceEquals(player.Map, master.Map), player.Combat.IsInCombat,
                new Vector3(player.X, player.Y, player.Z), new Vector3(master.X, master.Y, master.Z), Now, out long droppedMs))
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.FollowDropped, string.Create(CultureInfo.InvariantCulture,
                $"master {master.Name} is {Distance(player, master):F0} yd away and the bot has not followed for {PlayerbotPartyAnomalies.Seconds(droppedMs + PlayerbotFollowDropWatch.NotFollowedMs)}s (mode {_mode}, doing: {Activity(player)}, goal {Goal})"));

        // A party bot follows its master's lead: it never weighs a fight on its own and stays while the group fights. Only a wiping
        // group (World:Playerbots:Risk:PartyRetreatOnWipe) sends it back the way it came, past the creatures' leash.
        _risk.Track(player);
        PlayerbotNavigation.Guard(player, null); // a party bot goes where its master goes
        if (_risk.UpdateRetreat(player, interval))
        {
            Goal = PlayerbotGoalKind.Retreat;
            return;
        }

        if (_options.Risk.Enabled && _options.Risk.PartyRetreatOnWipe && player.Combat.IsInCombat && GroupWiping(player, master))
        {
            var enemies = new List<Creature>();
            foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
                if (unit is Creature creature && creature.IsAlive && ReferenceEquals(creature.Map, player.Map) && !enemies.Contains(creature))
                    enemies.Add(creature);
            if (_target is Creature { IsAlive: true } fighting && !enemies.Contains(fighting)) enemies.Add(fighting);
            if (enemies.Count > 0)
            {
                _wipe = master is { IsAlive: false } ? "master-dead" : "group-wipe";
                StopAttacking(player);
                _risk.StartRetreat(player, enemies, _wipe);
                Goal = PlayerbotGoalKind.Retreat;
                _risk.UpdateRetreat(player, interval);
                return;
            }
        }

        if (_restUntilMs != 0 && ContinueRest(player, master)) return;

        Unit? target = _mode == PlayerbotPartyMode.Passive ? null : SelectTarget(player, master);
        if (target is not null)
        {
            Fight(player, target, interval);
            return;
        }

        if (_target is not null)
        {
            if (_attacking) StopAttacking(player);
            _target = null;
            _attacking = false;
            _route = null;
            _rangedIdleMs = 0;
        }

        TargetEntry = 0;
        if (_mode == PlayerbotPartyMode.Passive && player.Combat.Victim is not null) StopAttacking(player);
        if (_mode == PlayerbotPartyMode.Stay && _stayPoint is null && (player.Movement.Flags & MovementFlags.MaskMoving) == 0)
            _stayPoint = new Vector3(player.X, player.Y, player.Z); // the place to hold: where the bot came to a stop
        if (master is null)
        {
            PlayerbotMovementControl.Stop(_session, player);
            Goal = PlayerbotGoalKind.Follow;
            return;
        }

        if (!player.Combat.IsInCombat)
        {
            if (ReleaseRoundRobinLoot(player, interval)) return;
            if (Resurrect(player, interval)) return;
            bool settled = _mode == PlayerbotPartyMode.Stay
                ? (_stayPoint is not { } sp || Vector3.Distance(new Vector3(player.X, player.Y, player.Z), sp) <= 1.5f)
                : (ReferenceEquals(player.Map, master.Map) && Distance(player, master) <= _followDistance + 4f);
            if (settled && _combatSpells.UpdateOutOfCombat(player, interval)) return;
            if (Rest(player, master)) return;
            if (settled) UpdateEquipment(player);
        }

        Follow(player, master, interval);
    }

    /// <summary>
    /// Cast a resurrection spell on a dead party member (or its corpse when it has released) on the same map within 40 yards,
    /// walking into the 28-yard cast range first when needed (vmangos PartyBotAI::UpdateOutOfCombatAI, PlayerbotGroupAI.Resurrect).
    /// </summary>
    private bool Resurrect(Player player, uint interval)
    {
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) return false;
        if (Services.Spells?.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting })
            return true;
        if (Now < _resurrectAtMs) return false;
        if (ResurrectionSpell(player) is not { } spell) return false;

        Player? dead = null;
        Corpse? corpse = null;
        WorldObject? body = null;
        float best = float.MaxValue;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid || _session.World.FindOnlinePlayer(slot.Guid) is not { } member) continue;
            if (member.IsAlive || !ReferenceEquals(member.Map, player.Map)) continue;
            if (ArcaneCore.Game.Death.Resurrection.ResurrectionRequests.IsRequested(member)) continue;
            Corpse? c = (member.Flags & PlayerFlags.Ghost) != 0 ? member.Combat.Corpse : null;
            if ((member.Flags & PlayerFlags.Ghost) != 0 && (c is null || !c.IsInWorld || !ReferenceEquals(c.Map, player.Map))) continue;
            WorldObject candidate = (WorldObject?)c ?? member;
            float d = Distance(player, candidate);
            if (d <= 40f && d < best)
            {
                best = d;
                dead = member;
                corpse = c;
                body = candidate;
            }
        }

        if (dead is null || body is null) return false;
        if (best > 28f)
        {
            if (_mode == PlayerbotPartyMode.Stay) return false;
            MoveTo(player, new Vector3(body.X, body.Y, body.Z), interval);
            return true;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        _route = null;
        bool cast = corpse is not null
            ? _combatSpells.CastAtCorpse(player, spell, corpse)
            : _combatSpells.CastAt(player, spell, dead);
        if (!cast) return false;
        LastResurrection = dead.Name;
        LastResurrectionAtCorpse = corpse is not null;
        _resurrectAtMs = Now + 3_000;
        return true;
    }

    internal SpellInfo? ResurrectionSpell(Player player)
    {
        if (!PlayerbotParty.IsHealerClass(player.Class) || Services.Spells is not { } feature) return null;
        SpellInfo? best = null;
        foreach (uint id in feature.Spellbook.GetSpells(player))
            if (feature.System.Store.Get(id) is { } spell && Groups.PlayerbotGroupAI.ResurrectionSpells.Contains(spell.Name, StringComparer.Ordinal)
                && spell.Reagents.All(r => r.Item <= 0 || r.Count == 0 || player.Inventory.GetItemCount((uint)r.Item) >= r.Count)
                && (best is null || spell.Id > best.Id))
                best = spell;
        return best;
    }

    private Unit? SelectTarget(Player player, Player? master)
    {
        if (player.Map is not { } map) return null;
        GroupManager? groups = Services.Social?.Groups;
        Unit? ordered = _ordered.IsEmpty ? null : map.FindObject(_ordered) as Unit;
        if (ordered is not null && !IsValidTarget(player, ordered)) ordered = null;
        if (ordered is null) _ordered = ObjectGuid.Empty;
        bool stay = _mode == PlayerbotPartyMode.Stay;
        bool masterHere = !stay && master is not null && ReferenceEquals(master.Map, map);
        // The master's victim: a creature, or a player the group may fight (the master attacks it, so it is the group's fight: GroupProvoked).
        Unit? masterVictim = masterHere && master!.Combat.Victim is { } victim
            && (victim is Creature || (victim is Player pv && PlayerbotHostility.MayEngage(player, pv, PlayerbotEngageReason.GroupProvoked, groups)))
            ? victim : null;
        // A player attacker is fought back only when it provoked the bot, as for a real player (PlayerbotHostility.MayEngage).
        IEnumerable<Unit> own = player.Combat.Attackers.Where(attacker => attacker is Creature
                || (attacker is Player p && PlayerbotHostility.MayEngage(player, p, PlayerbotEngageReason.Provoked, groups)))
            .OrderBy(attacker => Distance(player, attacker));
        IEnumerable<(Unit, float)> members = stay ? [] : MemberAttackers(player, map, groups);
        // vmangos PartyBotAI::SelectAttackTarget (PartyBotAI.cpp:366-377): in combat, the leader's raid marks come before its victim. An explicit
        // 'attack' order still wins over a mark.
        Unit? selected = ordered is not null && !stay ? ordered : null;
        if (selected is null && !stay && groups?.GetGroup(player.Guid) is { } group
            && (player.Combat.IsInCombat || (master is not null && master.Combat.Victim is not null)))
            selected = PlayerbotParty.MarkedTarget(group.TargetIcons, FocusIcons, guid => map.FindObject(guid) as Unit,
                unit => Distance(player, unit) <= PlayerbotParty.MarkRangeYards && IsValidTarget(player, unit));
        // A target the bot found no path to is skipped when the bot picks it itself (attackers of its own or of a member); the leader's explicit
        // choices (an 'attack'/assist order, a raid mark, the leader's own victim) are honoured whatever the memory says.
        uint now = unchecked((uint)Now);
        bool Pickable(Unit unit) => IsValidTarget(player, unit) && (ReferenceEquals(unit, masterVictim) || !_unreachable.Has(unit.Guid, now));
        selected ??= PlayerbotParty.SelectAttackTarget(null, masterVictim, own, members, Pickable);
        // FightThrough: what blocks the way to the master is fought, not run past (World:Playerbots:Follow:CatchUpMode).
        if (selected is null && !stay && _follower.Blocker(player) is { } blocker && Pickable(blocker)) selected = blocker;
        return selected;
    }

    /// <summary>The raid icons the bot sticks to in a fight, in order (vmangos <c>m_marksToFocus</c>; skull by default).</summary>
    private static readonly byte[] FocusIcons = [PlayerbotParty.SkullIcon];

    private IEnumerable<(Unit, float)> MemberAttackers(Player player, Map map, GroupManager? groups)
    {
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) yield break;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid || _session.World.FindOnlinePlayer(slot.Guid) is not { } member || !ReferenceEquals(member.Map, map)) continue;
            foreach (Unit attacker in member.Combat.Attackers)
            {
                if (attacker is Creature || (attacker is Player p && PlayerbotHostility.MayEngage(player, p, PlayerbotEngageReason.GroupProvoked, groups)))
                    yield return (attacker, Distance(player, attacker));
            }
        }
    }

    /// <summary>
    /// vmangos IsValidHostileTarget: in the world, alive, on the bot's map and attackable by it. A player is a valid target only under the one
    /// hostility rule (<see cref="PlayerbotHostility.IsHostilePlayer"/>); whether the bot may start on it is decided where the candidates are taken.
    /// </summary>
    private static bool IsValidTarget(Player player, Unit target)
        => target switch
        {
            Creature creature => creature.IsInWorld && creature.IsAlive && player.Map is { } map && ReferenceEquals(creature.Map, map)
                && float.IsFinite(creature.X) && float.IsFinite(creature.Y) && float.IsFinite(creature.Z)
                && map.Combat.Hooks.CanAttack(player, creature),
            Player other => PlayerbotHostility.IsHostilePlayer(player, other),
            _ => false,
        };

    private void Fight(Player player, Unit target, uint interval)
    {
        if (!ReferenceEquals(_target, target))
        {
            _target = target;
            _attacking = false;
            _route = null;
            _rangedIdleMs = 0;
            _follower.Suspend(player); // the fight takes the bot off its follow route (a fall under way lands first)
        }

        Goal = PlayerbotGoalKind.Assist;
        TargetEntry = target is Creature creature ? creature.Entry : 0;

        // A hurt ranged bot kites within 30 yards of its master (never in Stay mode: that holds the bot where it is).
        if (_mode != PlayerbotPartyMode.Stay)
        {
            Player? kiteMaster = _session.World.FindOnlinePlayer(_master);
            Vector3? centre = kiteMaster is not null && ReferenceEquals(kiteMaster.Map, player.Map) ? new Vector3(kiteMaster.X, kiteMaster.Y, kiteMaster.Z) : null;
            if (_kite.Update(player, target, interval, centre, KiteLeashYards, _risk, grouped: true))
            {
                _route = null;
                _rangedIdleMs = 0;
                return;
            }
        }
        else
        {
            _kite.Reset(player);
        }

        if (_combatSpells.Update(player, target, interval))
        {
            if ((player.Movement.Flags & MovementFlags.MaskMoving) == 0)
                FaceTargetIfNeeded(player, target);
            _rangedIdleMs = 0;
            return;
        }

        float distance = Distance(player, target);
        float preferredRange = _kite.HoldRange(player, _combatSpells.PreferredRange(player));
        bool engaged = player.Combat.ThreatenedBy.Contains(target) || target.Combat.Victim is not null;
        PlayerbotFightPosition position = PlayerbotBrain.DecidePosition(preferredRange, distance,
            player.Class == Class.Hunter, ReferenceEquals(target.Combat.Victim, player),
            idleTooLong: _rangedIdleMs >= PlayerbotBrain.RangedIdleLimitMs && !engaged,
            inMeleeReach: MapCombat.CanReachWithMeleeAutoAttack(player, target));

        switch (position)
        {
            case PlayerbotFightPosition.Hold:
                _rangedIdleMs = engaged ? 0 : _rangedIdleMs + interval;
                _route = null;
                if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
                    PlayerbotMovementControl.Stop(_session, player);
                FaceTargetIfNeeded(player, target);
                return;
            case PlayerbotFightPosition.ChaseToRange:
            case PlayerbotFightPosition.ChaseToMelee:
                if (_mode == PlayerbotPartyMode.Stay)
                {
                    PlayerbotMovementControl.Stop(_session, player);
                    FaceTargetIfNeeded(player, target);
                    return;
                }

                if (!MoveTo(player, new Vector3(target.X, target.Y, target.Z), interval) && target is Creature unreachable)
                {
                    _follower.NoteUnreachable(unreachable);
                    NoteUnreachableTarget(unreachable);
                }

                return;
        }

        _rangedIdleMs = 0;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            _route = null;
            return;
        }

        FaceTargetIfNeeded(player, target);
        if (!_attacking || !ReferenceEquals(player.Combat.Victim, target))
            _attacking = Act(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(target.Guid.Value));
    }

    /// <summary>The bot found no path to <paramref name="target"/>: it is skipped by <see cref="SelectTarget"/> for <see cref="UnreachableTargetMs"/> (a target the leader chose is still honoured).</summary>
    internal void NoteUnreachableTarget(Creature target) => _unreachable.Mark(target.Guid, unchecked((uint)Now), UnreachableTargetMs);

    private void FaceTargetIfNeeded(Player player, Unit target)
    {
        float flatDist = MathF.Sqrt(MathF.Pow(target.X - player.X, 2) + MathF.Pow(target.Y - player.Y, 2));
        if (flatDist > CombatConstants.NoFacingChecksDistance && !MapCombat.HasInArc(player, target, CombatConstants.AutoAttackArc))
        {
            PlayerbotMotion.Face(_session, player, MathF.Atan2(target.Y - player.Y, target.X - player.X));
        }
    }

    private void StopAttacking(Player player)
    {
        if (player.Combat.Victim is not null) Act(WorldOpcode.CmsgAttackstop, [], budgeted: false);
        _attacking = false;
        _target = null;
        _route = null;
        _rangedIdleMs = 0;
        TargetEntry = 0;
    }

    private void Follow(Player player, Player master, uint interval)
    {
        Goal = PlayerbotGoalKind.Follow;
        if (_mode == PlayerbotPartyMode.Stay)
        {
            // Stay holds whatever the master does: back to its place when something moved it (loot), else stand.
            _follower.Reset(player);
            if (HoldStayPoint(player, interval)) return;
            _route = null;
            PlayerbotMovementControl.Stop(_session, player);
            return;
        }

        _followDrop.NoteFollow(Now);
        PlayerbotFollowAction action = _follower.Update(player, master, _mode, _followAngle, _followDistance, interval);
        if (action == PlayerbotFollowAction.Hold && _comeRequested && _mode == PlayerbotPartyMode.Follow)
        {
            // 'come': arrived beside the master, now hold here.
            _comeRequested = false;
            _mode = PlayerbotPartyMode.Stay;
        }
    }

    /// <summary>
    /// Stay: the place is where the bot came to a stop after the order. A bot that left it (to release loot) walks back; true while
    /// it does.
    /// </summary>
    private bool HoldStayPoint(Player player, uint interval)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        if (_stayPoint is not { } point || Vector3.Distance(here, point) <= 1.5f) return false;
        if (MoveTo(player, point, interval)) return true;
        _stayPoint = here; // no way back: this is the place now
        return false;
    }

    /// <summary>Walk towards <paramref name="goal"/>, planning again when it moved more than 2 yards since the last plan.</summary>
    private bool MoveTo(Player player, Vector3 goal, uint interval)
    {
        if (_route is null || _route.Complete || Vector3.Distance(_routeGoal, goal) > 2f)
        {
            PlanOutcome planned = PlayerbotNavigation.PlanExact(player, goal, _options, ref _tileWait, _session.World.NowMs, out PlayerbotRoute? plannedRoute);
            _route = plannedRoute;
            if (planned != PlanOutcome.Route)
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                // A tile of the way is still being read: the goal is kept and planned to again next think, not reported unreachable.
                return planned == PlanOutcome.Pending;
            }

            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, interval, _session.World.NowMs)) _route = null;
        return true;
    }

    // --- loot ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// vmangos PartyBotAI.cpp:565-585 unassigns the bot's round-robin loot on every party kill (SMSG_PARTYKILLLOG), whoever made it,
    /// "so real players can loot". ArcaneCore sends that packet to the killer alone, so instead the bot looks over the corpses it can
    /// see for loot it holds (<see cref="LootBag.Owner"/>) at every think out of combat, whatever it fought or was told. For each it
    /// does what a player does to give up a turn: it walks up, opens the corpse and releases it untouched (CMSG_LOOT,
    /// CMSG_LOOT_RELEASE), and the loot service opens the leftovers to the whole group. A release that the shared action budget
    /// held back is tried on the next think; a corpse that could not be reached or opened <see cref="MaxReleaseAttempts"/> times is left.
    /// </summary>
    private bool ReleaseRoundRobinLoot(Player player, uint interval)
    {
        if (FindHeldLoot(player) is not { } corpse) return false;
        Goal = PlayerbotGoalKind.Loot;
        if (Distance(player, corpse) > MeleeReach)
        {
            if (!MoveTo(player, new Vector3(corpse.X, corpse.Y, corpse.Z), interval)) CountReleaseTry(corpse.Guid);
            return true;
        }

        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        _route = null;
        byte[] guid = PlayerbotNavigation.GuidPayload(corpse.Guid.Value);
        if (!Act(WorldOpcode.CmsgLoot, guid)) return true; // the budget is spent: the corpse is still held, so the next think tries again
        Act(WorldOpcode.CmsgLootRelease, guid, budgeted: false);
        CountReleaseTry(corpse.Guid); // released: no longer held, so never found again; refused: one try spent
        return true;
    }

    /// <summary>The nearest corpse the bot can see whose round-robin loot it holds (not yet looted out), within <see cref="RoundRobinReach"/>.</summary>
    private Creature? FindHeldLoot(Player player)
    {
        if (player.Map is not { } map || Services.LootOf(map) is not { } loot) return null;
        Creature? nearest = null;
        float best = RoundRobinReach;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature { IsAlive: false } corpse) continue;
            if (loot.FindLoot(guid) is not { } bag || bag.Owner != player.Guid || bag.IsClosed || bag.IsEmpty) continue;
            if (_releaseTries.TryGetValue(guid, out int tries) && tries >= MaxReleaseAttempts) continue;
            float distance = Distance(player, corpse);
            if (float.IsFinite(distance) && distance <= best)
            {
                best = distance;
                nearest = corpse;
            }
        }

        return nearest;
    }

    private void CountReleaseTry(ObjectGuid corpse)
    {
        if (_releaseTries.Count >= 64) _releaseTries.Clear(); // corpses decay; a long session must not grow this
        _releaseTries[corpse] = _releaseTries.GetValueOrDefault(corpse) + 1;
    }

    // --- rest ---------------------------------------------------------------------------------------------------------

    /// <summary>Eat or drink out of combat (PlayerbotConsumables, as the brain does), unless the master is walking away.</summary>
    private bool Rest(Player player, Player master)
    {
        SpellFeature? spells = Services.Spells;
        if (spells is null || player.Combat.IsInCombat || !NearMaster(player, master)
            || spells.System.GetState(player.Guid)?.CurrentCast is { State: SpellCastState.Preparing or SpellCastState.Casting }
            || PlayerbotConsumables.IsEatingOrAboutTo(player, spells.System)
            || !PlayerbotConsumables.TryFindRecovery(player, spells.System, out PlayerbotConsumable consumable)) return false;
        Goal = PlayerbotGoalKind.Rest;
        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            return true;
        }

        if (player.StandState != StandState.Sit)
        {
            Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(1u));
            return true;
        }

        Item item = consumable.Item;
        if (!Act(WorldOpcode.CmsgUseItem, PlayerbotNavigation.UseItemPayload(item.BagSlot, item.Slot))) return true;
        _restKind = consumable.Kind;
        _restUntilMs = unchecked(_session.World.NowMs + 20_000);
        return true;
    }

    private bool ContinueRest(Player player, Player? master)
    {
        if (!PlayerbotMovementControl.Stop(_session, player)) return true;
        SpellFeature? spells = Services.Spells;
        bool eating = spells is not null && PlayerbotConsumables.IsEatingOrAboutTo(player, spells.System);
        if (!player.Combat.IsInCombat && eating && PlayerbotConsumables.NeedsRecovery(player, _restKind)
            && unchecked((int)(_restUntilMs - _session.World.NowMs)) > 0 && master is not null && NearMaster(player, master))
        {
            Goal = PlayerbotGoalKind.Rest;
            return true;
        }

        if (!Act(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u))) return true;
        _restUntilMs = 0;
        return false;
    }

    private static bool NearMaster(Player player, Player master)
        => ReferenceEquals(player.Map, master.Map) && Distance(player, master) <= RestBreakDistance;

    // --- death --------------------------------------------------------------------------------------------------------

    private void RetireDeath()
    {
        _target = null;
        _attacking = false;
        _route = null;
        _restUntilMs = 0;
        _corpseRun = false;
        _thinkElapsed = 0;
        TargetEntry = 0;
        _combatSpells.Reset();
        _kite.Reset(_session.Player);
        _unreachable.Clear();
    }

    /// <summary>
    /// Dead: a member's resurrection is accepted at intake. Otherwise, with <see cref="PlayerbotPartyOptions.AutoRevive"/>, the bot
    /// revives in place at half health when vmangos ShouldAutoRevive allows it (ResurrectPlayer(0.5f) + SpawnCorpseBones; a bot that
    /// is already a ghost when it is grouped revives where it stands), and waits while it does not (a member fights, or a healer could
    /// resurrect it) for at most <see cref="DeadWaitMs"/>. vmangos would wait for ever; here, once that wait runs out, or at once
    /// without AutoRevive, the bot releases and runs back to its body like the brain (<see cref="PlayerbotRecovery"/>), and it keeps
    /// to that corpse run until it is alive: the released ghost is not revived at the graveyard.
    /// </summary>
    private void UpdateDead(Player player, uint interval)
    {
        Goal = PlayerbotGoalKind.Recover;
        long now = Now;
        if (_deadSinceMs < 0) _deadSinceMs = now;
        if (_deadEpisode.Observe(true, now, DeadWaitMs + PlayerbotPartyAnomalies.DeathRecoveryBudgetMs, PlayerbotPartyAnomalies.MaxGapMs))
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.DeathUnrecovered, string.Create(CultureInfo.InvariantCulture,
                $"dead {PlayerbotPartyAnomalies.Seconds(now - _deadSinceMs)}s and still not alive (ghost {(player.Flags & PlayerFlags.Ghost) != 0}, corpse run {_corpseRun}, auto-revive {_options.Party.AutoRevive})"));
        if (_options.Party.AutoRevive && !_corpseRun && player.Map is { } map)
        {
            bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
            if (PlayerbotParty.ShouldAutoRevive(ghost, ReviveMembers(player)))
            {
                map.Combat.ResurrectPlayer(player, 0.5f, applySickness: false);
                if (player.IsAlive) (player.Combat.Corpse?.Map ?? map).Combat.SpawnCorpseBones(player);
                _recovery.Reset();
                return;
            }

            if (now - _deadSinceMs < DeadWaitMs) return;
            _corpseRun = true;
        }

        _recovery.Update(player, interval);
    }

    private IEnumerable<PlayerbotReviveMember> ReviveMembers(Player player)
    {
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) yield break;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid || _session.World.FindOnlinePlayer(slot.Guid) is not { } member) continue;
            float? distance = ReferenceEquals(member.Map, player.Map) && member.Map is not null ? Distance(player, member) : null;
            yield return new PlayerbotReviveMember(member.Combat.IsInCombat, member.IsAlive, PlayerbotParty.IsHealerClass(member.Class), distance);
        }
    }

    // --- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>A whisper to <paramref name="to"/> in the bot's own language (Universal is refused outside AFK/DND replies).</summary>
    private void Tell(Player player, string to, string text)
    {
        if (to.Length == 0) return;
        Language language = player.Team == Team.Horde ? Language.Orcish : Language.Common;
        if (!Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Whisper(language, to, text), budgeted: false))
            PlayerbotPartyAnomalies.Report(player, PlayerbotPartyAnomalies.ReplyLost, $"the whisper to {to} could not be sent: {text}", AnomalySeverity.Info);
    }

    /// <summary>
    /// Run one client opcode through its world handler. <paramref name="budgeted"/> actions draw on the shared per-tick action budget
    /// like the brain's; the others are a client's own answers (to an invitation, a roll, a resurrection, a command) and never wait.
    /// </summary>
    private bool Act(WorldOpcode opcode, byte[] payload, bool budgeted = true)
    {
        if (budgeted) return _session.TryManagedAction(opcode, payload);
        ManagedActionBudget? budget = _session.ManagedBudget;
        _session.ManagedBudget = null;
        try
        {
            return _session.TryManagedAction(opcode, payload);
        }
        finally
        {
            _session.ManagedBudget = budget;
        }
    }

    /// <summary>The world features the party AI uses (any may be absent in a minimal host).</summary>
    private sealed class PartyServices(WorldSession session)
    {
        private bool _socialResolved, _teleportsResolved, _spellsResolved, _questsResolved, _flightsResolved, _lootResolved;
        private SocialFeature? _socialFeature;
        private SocialContext? _socialContext;
        private TeleportService? _teleports;
        private SpellFeature? _spells;
        private ArcaneCore.Game.Npc.QuestNpcServices? _quests;
        private TaxiFlightSystem? _flights;
        private GameObjectLootFeature? _lootFeature;

        public SocialContext? Social
        {
            get
            {
                if (_socialContext is not null) return _socialContext;
                if (!_socialResolved)
                {
                    _socialFeature = session.Services.GetService<SocialFeature>();
                    _socialResolved = true;
                }

                return _socialFeature is { } social ? _socialContext = SafeContext(social) : null;
            }
        }

        public TeleportService? Teleports
        {
            get
            {
                if (_teleportsResolved) return _teleports;
                _teleportsResolved = true;
                return _teleports = session.Services.GetService<TeleportFeature>()?.Teleports;
            }
        }

        public SpellFeature? Spells
        {
            get
            {
                if (_spellsResolved) return _spells;
                _spellsResolved = true;
                return _spells = session.Services.GetService<SpellFeature>();
            }
        }

        public ArcaneCore.Game.Npc.QuestNpcServices? Quests
        {
            get
            {
                if (_questsResolved) return _quests;
                _questsResolved = true;
                return _quests = session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services;
            }
        }

        public TaxiFlightSystem? Flights
        {
            get
            {
                if (_flightsResolved) return _flights;
                _flightsResolved = true;
                return _flights = session.Services.GetService<NpcServicesFeature>()?.Flights;
            }
        }

        public LootService? LootOf(Map map)
        {
            if (!_lootResolved)
            {
                _lootFeature = session.Services.GetService<GameObjectLootFeature>();
                _lootResolved = true;
            }

            return _lootFeature?.FindSystem(map)?.Loot;
        }

        private static SocialContext? SafeContext(SocialFeature social)
        {
            try
            {
                return social.Context;
            }
            catch (InvalidOperationException ex)
            {
                FaultLedger.Shared.Record("playerbot.party", ex);
                return null; // not attached
            }
        }
    }
}

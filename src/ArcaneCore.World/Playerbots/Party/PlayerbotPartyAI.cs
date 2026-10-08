using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

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
/// player when the leader is a bot) this AI drives the bot. It follows at 2-5 yards, teleports (out of combat) to a master more than
/// 100 yards away or on another map (the teleport service the GM .goname/.namego commands use, with its instance checks), fights what
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
    ];

    /// <summary>The brain's melee approach distance (PlayerbotBrain: chase until within 4 yards, then swing).</summary>
    internal const float MeleeReach = 4f;

    /// <summary>A resting bot gets up and follows when its master is farther than this.</summary>
    internal const float RestBreakDistance = 30f;

    /// <summary>How long a dead bot waits for a resurrection (vmangos waits while ShouldAutoRevive says no) before it runs back.</summary>
    internal const long DefaultDeadWaitMs = 120_000;

    /// <summary>
    /// Round-robin loot the bot holds is released when its corpse is this close: the group reward distance (vmangos
    /// IsAtGroupRewardDistance, 74 yards), within which the bot was when the loot was given to it.
    /// </summary>
    internal const float RoundRobinReach = 74f;

    /// <summary>A corpse whose loot the bot could not release after this many tries (walk failed, or the open was refused) is left.</summary>
    internal const int MaxReleaseAttempts = 3;

    /// <summary>A refused teleport (a full or locked instance) is tried again after this long.</summary>
    internal const long TeleportRetryMs = 10_000;

    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _combatSpells;
    private readonly PlayerbotRecovery _recovery;
    private readonly PlayerbotRisk _risk;
    private string? _wipe;
    private readonly PlayerbotReplyLimiter _replies = new();
    private readonly Random _random;
    private ObjectGuid _master;
    private string _masterName = string.Empty;
    private bool _engaged;
    private long _masterLostMs = -1;
    private PlayerbotPartyMode _mode;
    private bool _comeRequested;
    private ObjectGuid _ordered;
    private PlayerbotRoute? _route;
    private Vector3 _routeGoal;
    private float _followAngle;
    private float _followDistance = 3f;
    private Creature? _target;
    private bool _attacking;
    private uint _thinkElapsed;
    private uint _restUntilMs;
    private PlayerbotConsumableKind _restKind;
    private long _deadSinceMs = -1;
    private bool _deathRetired;
    private bool _corpseRun;
    private readonly Dictionary<ObjectGuid, int> _releaseTries = [];
    private Vector3? _stayPoint;
    private long _teleportRetryAtMs;
    private uint _idleIntakeAtMs;
    private bool _idleIntakeStarted;
    private readonly Chat.PlayerbotChat? _chat;
    private readonly Guid _botId;
    private readonly Chat.PlayerbotChatEvents _chatEvents = new();

    internal PlayerbotPartyAI(WorldSession session, PlayerbotOptions options, Chat.PlayerbotChat? chat = null, Guid botId = default)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _combatSpells = new PlayerbotCombatSpells(session);
        _recovery = new PlayerbotRecovery(session, options);
        _risk = new PlayerbotRisk(session, options, _combatSpells);
        _random = new Random(session.AccountId);
        _chat = chat;
        _botId = botId;
    }

    /// <summary>
    /// What the bot does when it is not party-driven (the brain's goal and quest; set by <see cref="ManagedPlayerbotFeature"/>), for
    /// the chat persona. Null: the party AI's own goal only.
    /// </summary>
    internal Func<(PlayerbotGoalKind Goal, uint QuestId)>? BrainGoal { get; set; }

    /// <summary>
    /// An invitation the bot accepts whatever <see cref="PlayerbotPartyOptions.InvitePolicy"/> says (bot, inviter): another bot's
    /// invitation into a group the coordinator formed for both (<see cref="Groups.PlayerbotGroupCoordinator"/>). Null: none.
    /// </summary>
    internal Func<Player, Player, bool>? AcceptsBotGroup { get; set; }

    /// <summary>
    /// The vote of a bot in a bot-led group on a group loot roll (bot, item): need what it would wear, greed the rest. Null, or a null
    /// answer: <see cref="PlayerbotPartyOptions.LootRoll"/>.
    /// </summary>
    internal Func<Player, uint, RollVote?>? GroupLootVote { get; set; }

    /// <summary>Whether the party AI drives the bot now (it has a master, or is waiting out a departed one).</summary>
    internal bool IsEngaged => _engaged;

    /// <summary>The master's GUID while engaged, otherwise empty.</summary>
    internal ObjectGuid Master => _master;

    internal string MasterName => _masterName;

    internal PlayerbotPartyMode Mode => _mode;

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Follow;

    internal uint TargetEntry { get; private set; }

    internal Creature? InspectionTarget => _target;

    /// <summary>
    /// How long a dead bot waits for a resurrection before it runs back to its body (<see cref="DefaultDeadWaitMs"/>; a test seam,
    /// never configuration-bound).
    /// </summary>
    internal long DeadWaitMs { get; set; } = DefaultDeadWaitMs;

    private PartyServices Services => new(_session);

    private long Now => (long)_session.World.Uptime.TotalMilliseconds;

    // --- intake ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// Take this tick's invitations, chat lines, loot rolls and resurrection offers out of the bot's capture queue and answer them
    /// (world thread; every tick for every autonomous bot). The answers are a client's own replies: they do not wait for the shared
    /// action budget.
    /// </summary>
    internal void Intake(Player player)
    {
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
                case WorldOpcode.SmsgResurrectRequest: OnResurrectRequest(player, packet.Payload); break;
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
        if (Services.Social is not { } social || social.Groups.GetInvite(player.Guid) is not { } group) return; // already answered
        string? name = PlayerbotGroupInvites.ReadInviter(payload);
        Player? inviter = (name is null ? null : _session.World.FindOnlinePlayer(name)) ?? _session.World.FindOnlinePlayer(group.LeaderGuid);
        PlayerbotPartyOptions party = _options.Party;
        bool accept = inviter is not null && (AcceptsBotGroup?.Invoke(player, inviter) == true || PlayerbotGroupInvites.Allows(party.InvitePolicy, party.Allowlist, inviter.Name,
            PlayerbotGroupInvites.SameGuild(social, player, inviter), PlayerbotGroupInvites.OnBotsFriendList(social, player, inviter)));
        Act(accept ? WorldOpcode.CmsgGroupAccept : WorldOpcode.CmsgGroupDecline, [], budgeted: false);
    }

    private void OnChat(Player player, byte[] payload)
    {
        if (!PlayerbotChatCommands.TryRead(payload, out PlayerbotChatLine line)) return;
        Player? sender = _session.World.FindOnlinePlayer(line.Sender);
        bool senderIsBot = sender?.Session is WorldSession { IsManaged: true };
        ObjectGuid master = ResolveMaster(player);
        switch (PlayerbotChatCommands.Classify(line, player.Guid, master, senderIsBot))
        {
            case PlayerbotChatSource.Master when sender is not null:
                // The master's order counts from the moment the group has a master, before the bot's first budgeted turn: engaging
                // here keeps that turn from resetting the order (Drives engages only a master it is not engaged to yet).
                if (!_engaged || master != _master) Engage(player, master);
                _masterLostMs = -1;
                if (PlayerbotChatCommands.TryParse(line.Text, out PlayerbotPartyCommand command)) Execute(player, sender, command);
                else if (Converse(player, line, sender, master)) { }
                else if (line.Type == ChatType.Whisper && _replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.Help);
                break;
            case PlayerbotChatSource.Stranger when sender is not null:
                if (Converse(player, line, sender, master)) { }
                else if (_replies.TryTake(sender.Guid, Now)) Tell(player, sender.Name, PlayerbotChatCommands.PoliteReply);
                break;
            case PlayerbotChatSource.Ignore when sender is not null && !senderIsBot && sender.Guid != player.Guid:
                // A party or say line from a real player: answered only when it names the bot (and chat is on).
                Converse(player, line, sender, master);
                break;
        }
    }

    // --- chat ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Hand a player's line to the chat service (docs/areas/playbots.md, Bot chat). False when chat is off or the line is not one the
    /// bots answer (the fixed replies of before then apply); true when the line is the chat's, whether or not a reply comes (a
    /// cooldown, a spent budget or a full queue means silence).
    /// </summary>
    private bool Converse(Player player, PlayerbotChatLine line, Player sender, ObjectGuid master)
    {
        if (_chat is not { IsActive: true } chat || !chat.Answers(line.Type) || line.Text.Length == 0) return false;
        bool whisper = line.Type == ChatType.Whisper;
        if (!whisper)
        {
            if (!Chat.PlayerbotChatPrompts.Addresses(line.Text, player.Name)) return false;
            if (line.Type == ChatType.Say && sender.Team != player.Team) return false;
        }

        _chatEvents.Observe(player, Now);
        bool fromMaster = !master.IsEmpty && sender.Guid == master;
        var ask = new Chat.BotChatAsk(_botId, player.Guid, Persona(player, master), sender.Guid, sender.Name,
            line.Type is ChatType.Raid or ChatType.RaidLeader ? ChatType.Party : line.Type, line.Text, fromMaster, InviteAllowed(player, sender));
        chat.TryAsk(ask);
        return true;
    }

    /// <summary>The bot's facts for the chat (world thread): who and where it is, what it does, its group and recent events.</summary>
    private Chat.BotChatPersona Persona(Player player, ObjectGuid master)
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
        return new Chat.BotChatPersona(player.Name, player.Race, player.Class, player.Gender, player.Level, zone, subzone, goal, quest,
            masterName, inGroup, _chatEvents.Recent(Now));
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
            return (null, null); // a minimal host without map data
        }
    }

    /// <summary>Whether the bot would accept an invitation from <paramref name="sender"/> (its invite policy).</summary>
    private bool InviteAllowed(Player player, Player sender)
    {
        if (Services.Social is not { } social) return false;
        PlayerbotPartyOptions party = _options.Party;
        return PlayerbotGroupInvites.Allows(party.InvitePolicy, party.Allowlist, sender.Name,
            PlayerbotGroupInvites.SameGuild(social, player, sender), PlayerbotGroupInvites.OnBotsFriendList(social, player, sender));
    }

    /// <summary>
    /// World thread: say a chat reply through the bot's own CMSG_MESSAGECHAT (a whisper back, the party line or /say), and carry out
    /// the master's order when the speaker is still the bot's master.
    /// </summary>
    internal void DeliverChat(Player player, Chat.BotChatReply reply)
    {
        if (reply.Text is { Length: > 0 } text)
        {
            Language language = player.Team == Team.Horde ? Language.Orcish : Language.Common;
            switch (reply.Channel)
            {
                case ChatType.Whisper:
                    if (_session.World.FindOnlinePlayer(reply.SenderGuid) is not null)
                        Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Whisper(language, reply.SenderName, text), budgeted: false);
                    break;
                case ChatType.Party:
                    if (Services.Social?.Groups.GetGroup(player.Guid) is not null)
                        Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(ChatType.Party, language, text), budgeted: false);
                    break;
                case ChatType.Say:
                    Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(ChatType.Say, language, text), budgeted: false);
                    break;
            }
        }

        if (reply.Command is { } command)
        {
            ObjectGuid master = ResolveMaster(player);
            if (master.IsEmpty || master != reply.SenderGuid || _session.World.FindOnlinePlayer(master) is not { } current) return;
            if (!_engaged || master != _master) Engage(player, master);
            _masterLostMs = -1;
            Execute(player, current, command, acknowledge: reply.AcknowledgeCommand);
        }
    }

    private void OnLootRoll(Player player, byte[] payload)
    {
        if (!PlayerbotLootRolls.TryRead(payload, out PlayerbotLootRolls.StartRoll roll)) return;
        RollVote vote = GroupLootVote?.Invoke(player, roll.ItemId) ?? PlayerbotLootRolls.VoteFor(_options.Party.LootRoll);
        Act(WorldOpcode.CmsgLootRoll, PlayerbotLootRolls.Vote(roll, vote), budgeted: false);
    }

    /// <summary>mangoszero AcceptResurrectAction.h: a resurrection from a member of the bot's group is accepted, any other declined.</summary>
    private void OnResurrectRequest(Player player, byte[] payload)
    {
        if (payload.Length < 8) return;
        var caster = new ObjectGuid(BitConverter.ToUInt64(payload, 0));
        bool member = caster != player.Guid && Services.Social?.Groups.AreInSameGroup(player.Guid, caster) == true;
        var response = new PacketWriter(9);
        response.WriteUInt64(caster.Value);
        response.WriteByte(member ? (byte)1 : (byte)0);
        Act(WorldOpcode.CmsgResurrectResponse, response.ToArray(), budgeted: false);
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
                PlayerbotMovementControl.Stop(_session, player);
                if (acknowledge) Tell(player, master.Name, "Staying here.");
                break;
            case PlayerbotPartyCommand.Attack:
                if (player.Map?.FindObject(master.Selection) is Creature target && IsValidTarget(player, target))
                {
                    _ordered = target.Guid;
                    if (_mode != PlayerbotPartyMode.Follow) _mode = PlayerbotPartyMode.Follow;
                    _comeRequested = false;
                    if (acknowledge) Tell(player, master.Name, "Attacking " + target.Template.Name + ".");
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
            case PlayerbotPartyCommand.Leave:
                Tell(player, master.Name, "Leaving the group.");
                Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
                break;
        }
    }

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

    /// <summary>What the bot is doing, in the words of its status answer.</summary>
    internal string Activity(Player player)
    {
        if (!player.IsAlive) return "dead";
        if (_restUntilMs != 0) return "resting";
        if (_target is { IsAlive: true } target) return "fighting " + target.Template.Name;
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
    internal ObjectGuid ResolveMaster(Player player)
    {
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) return ObjectGuid.Empty;
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
        ObjectGuid master = ResolveMaster(player);
        if (!master.IsEmpty)
        {
            if (!_engaged || master != _master) Engage(player, master);
            _masterLostMs = -1;
            return true;
        }

        if (!_engaged) return false;
        if (Services.Social?.Groups.GetGroup(player.Guid) is null)
        {
            Release(player);
            return false;
        }

        long now = Now;
        if (_masterLostMs < 0) _masterLostMs = now;
        if (now - _masterLostMs < _options.Party.MasterTimeoutSeconds * 1000L) return true;
        Act(WorldOpcode.CmsgGroupDisband, [], budgeted: false);
        Release(player);
        return false;
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
        _target = null;
        _attacking = false;
        _thinkElapsed = 0;
        _releaseTries.Clear();
        _followAngle = (float)(_random.NextDouble() * MathF.Tau);
        _followDistance = PlayerbotParty.MinFollowDistance
            + ((float)_random.NextDouble() * (PlayerbotParty.MaxFollowDistance - 0.5f - PlayerbotParty.MinFollowDistance));
        Goal = PlayerbotGoalKind.Follow;
        TargetEntry = 0;
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
        _releaseTries.Clear();
        _deadSinceMs = -1;
        _corpseRun = false;
        _recovery.Reset();
        _combatSpells.Reset();
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
            RetireDeath();
            _deathRetired = true;
        }

        if (PlayerbotMovementControl.Update(_session, player)) return;
        _thinkElapsed = unchecked(_thinkElapsed + elapsedMs);
        if (_thinkElapsed < _options.ThinkIntervalMs) return;
        uint interval = _thinkElapsed;
        _thinkElapsed = 0;

        if (PlayerbotMotion.ConsumeLoop(player)) _route = null;
        if (dead)
        {
            UpdateDead(player, interval);
            return;
        }

        _deathRetired = false;
        _deadSinceMs = -1;
        _corpseRun = false;
        _recovery.Reset();

        Player? master = _session.World.FindOnlinePlayer(_master);

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
            if (_target is { IsAlive: true } fighting && !enemies.Contains(fighting)) enemies.Add(fighting);
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

        Creature? target = _mode == PlayerbotPartyMode.Passive ? null : SelectTarget(player, master);
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
            if (Rest(player, master)) return;
        }

        Follow(player, master, interval);
    }

    private Creature? SelectTarget(Player player, Player? master)
    {
        if (player.Map is not { } map) return null;
        Creature? ordered = _ordered.IsEmpty ? null : map.FindObject(_ordered) as Creature;
        if (ordered is not null && !IsValidTarget(player, ordered)) ordered = null;
        if (ordered is null) _ordered = ObjectGuid.Empty;
        bool stay = _mode == PlayerbotPartyMode.Stay;
        Creature? masterVictim = !stay && master is not null && ReferenceEquals(master.Map, map) ? master.Combat.Victim as Creature : null;
        IEnumerable<Creature> own = player.Combat.Attackers.OfType<Creature>().OrderBy(attacker => Distance(player, attacker));
        IEnumerable<(Creature, float)> members = stay ? [] : MemberAttackers(player, map);
        return PlayerbotParty.SelectAttackTarget(stay ? null : ordered, masterVictim, own, members, unit => IsValidTarget(player, unit));
    }

    private IEnumerable<(Creature, float)> MemberAttackers(Player player, Map map)
    {
        if (Services.Social?.Groups.GetGroup(player.Guid) is not { } group) yield break;
        foreach (GroupMemberSlot slot in group.Members)
        {
            if (slot.Guid == player.Guid || _session.World.FindOnlinePlayer(slot.Guid) is not { } member || !ReferenceEquals(member.Map, map)) continue;
            foreach (Creature attacker in member.Combat.Attackers.OfType<Creature>())
                yield return (attacker, Distance(player, attacker));
        }
    }

    /// <summary>vmangos IsValidHostileTarget: in the world, alive, on the bot's map and attackable by it.</summary>
    private static bool IsValidTarget(Player player, Creature target)
        => target.IsInWorld && target.IsAlive && player.Map is { } map && ReferenceEquals(target.Map, map)
            && float.IsFinite(target.X) && float.IsFinite(target.Y) && float.IsFinite(target.Z)
            && map.Combat.Hooks.CanAttack(player, target);

    private void Fight(Player player, Creature target, uint interval)
    {
        if (!ReferenceEquals(_target, target))
        {
            _target = target;
            _attacking = false;
            _route = null;
        }

        Goal = PlayerbotGoalKind.Assist;
        TargetEntry = target.Entry;
        if (_combatSpells.Update(player, target, interval)) return;
        if (Distance(player, target) > MeleeReach)
        {
            if (_mode == PlayerbotPartyMode.Stay)
            {
                PlayerbotMovementControl.Stop(_session, player);
                return;
            }

            MoveTo(player, new Vector3(target.X, target.Y, target.Z), interval);
            return;
        }

        if ((player.Movement.Flags & MovementFlags.MaskMoving) != 0)
        {
            PlayerbotMovementControl.Stop(_session, player);
            _route = null;
            return;
        }

        if (!_attacking || !ReferenceEquals(player.Combat.Victim, target))
            _attacking = Act(WorldOpcode.CmsgAttackswing, PlayerbotNavigation.GuidPayload(target.Guid.Value));
    }

    private void StopAttacking(Player player)
    {
        if (player.Combat.Victim is not null) Act(WorldOpcode.CmsgAttackstop, [], budgeted: false);
        _attacking = false;
        _target = null;
        _route = null;
        TargetEntry = 0;
    }

    private void Follow(Player player, Player master, uint interval)
    {
        TeleportService? teleports = Services.Teleports;
        Map? masterMap = master.Map;
        bool sameMap = masterMap is not null && ReferenceEquals(player.Map, masterMap);
        bool otherInstance = !sameMap && masterMap is not null && player.Map is { } own && own.MapId == masterMap.MapId;
        bool masterInWorld = master.IsInWorld && masterMap is not null && teleports?.StageOf(master) is null;
        float distance = sameMap ? Distance(player, master) : float.NaN;
        PlayerbotFollowAction action = PlayerbotParty.DecideFollow(new PlayerbotFollowFacts(_mode, masterInWorld, sameMap, distance,
            _options.Party.TeleportToLeader, _options.AllowedMaps.Contains(master.MapId), Services.Flights?.IsFlying(master) == true,
            player.Combat.IsInCombat, otherInstance));
        if (action == PlayerbotFollowAction.Hold && _comeRequested && _mode == PlayerbotPartyMode.Follow)
        {
            // 'come': arrived beside the master, now hold here.
            _comeRequested = false;
            _mode = PlayerbotPartyMode.Stay;
        }

        Goal = PlayerbotGoalKind.Follow;
        if (action == PlayerbotFollowAction.Hold && _mode == PlayerbotPartyMode.Stay && HoldStayPoint(player, interval)) return;
        switch (action)
        {
            case PlayerbotFollowAction.Hold:
            case PlayerbotFollowAction.Wait:
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                break;
            case PlayerbotFollowAction.Teleport:
                TeleportToMaster(player, master, teleports);
                break;
            case PlayerbotFollowAction.Move:
                Vector3 goal = PlayerbotParty.FollowPoint(new Vector3(master.X, master.Y, master.Z), master.Orientation, _followAngle, _followDistance);
                if (!MoveTo(player, goal, interval)) MoveTo(player, new Vector3(master.X, master.Y, master.Z), interval);
                break;
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
            if (!PlayerbotNavigation.TryPlan(player, goal, _options, out _route))
            {
                _route = null;
                PlayerbotMovementControl.Stop(_session, player);
                return false;
            }

            _routeGoal = goal;
        }

        if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, interval, _session.World.NowMs)) _route = null;
        return true;
    }

    /// <summary>
    /// vmangos UpdateAI :806-817 (ChatHandler(me).HandleGonameCommand(leader)): the ordinary teleport service, so the map resolver
    /// puts the bot into the master's instance (the group's bind) or refuses it as it would refuse the GM command. The bot lands on
    /// the master's spot, not 5 yards above as .goname puts a GM: it has no client to fall.
    /// </summary>
    private void TeleportToMaster(Player player, Player master, TeleportService? teleports)
    {
        _route = null;
        if (teleports is null || teleports.IsBeingTeleported(player) || Now < _teleportRetryAtMs) return;
        // Within one map id the service teleports near and keeps the bot's own instance: that never reaches a master in another one.
        if (player.Map is { } own && own.MapId == master.MapId && !ReferenceEquals(own, master.Map))
        {
            _teleportRetryAtMs = Now + TeleportRetryMs;
            return;
        }

        PlayerbotMovementControl.Stop(_session, player);
        if (!teleports.TeleportTo(player, master.MapId, master.X, master.Y, master.Z, master.Orientation))
            _teleportRetryAtMs = Now + TeleportRetryMs;
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
            || PlayerbotConsumables.HasActiveFoodDrink(player, spells.System)
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
        bool eating = spells is not null && PlayerbotConsumables.HasActiveFoodDrink(player, spells.System);
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
        Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Whisper(language, to, text), budgeted: false);
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

    private static float Distance(WorldObject a, WorldObject b)
        => MathF.Sqrt(MathF.Pow(a.X - b.X, 2) + MathF.Pow(a.Y - b.Y, 2) + MathF.Pow(a.Z - b.Z, 2));

    /// <summary>The world features the party AI uses (any may be absent in a minimal host).</summary>
    private readonly struct PartyServices(WorldSession session)
    {
        public SocialContext? Social => session.Services.GetService<SocialFeature>() is { } social ? SafeContext(social) : null;

        public TeleportService? Teleports => session.Services.GetService<TeleportFeature>()?.Teleports;

        public SpellFeature? Spells => session.Services.GetService<SpellFeature>();

        public ArcaneCore.Game.Npc.QuestNpcServices? Quests => session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services;

        public TaxiFlightSystem? Flights => session.Services.GetService<NpcServicesFeature>()?.Flights;

        public LootService? LootOf(Map map) => session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot;

        private static SocialContext? SafeContext(SocialFeature social)
        {
            try
            {
                return social.Context;
            }
            catch (InvalidOperationException)
            {
                return null; // not attached
            }
        }
    }
}

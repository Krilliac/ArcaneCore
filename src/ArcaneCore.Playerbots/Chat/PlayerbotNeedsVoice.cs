using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// The bot voice (<c>World:Playerbots:Chat:Voice</c>, docs/areas/playbots.md, Bot voice): bots say what they want and need, from their
/// real state, the way 1.12 players did. Run by the social feature on its pass (once a second; <see cref="Run"/>), with bounded work: the
/// pass-wide topics read the group coordinator's waiting bots and runs and the running battlegrounds, then at most
/// <see cref="PlayerbotVoiceOptions.BotsPerPass"/> bots are looked at (those grouped with a real player first, the others round robin), and
/// at most <see cref="PlayerbotVoiceOptions.LinesPerPass"/> lines are handed on. Topics:
/// <list type="bullet">
/// <item>LFG / LFM for the group goals bots wait on (<see cref="PlayerbotGroupCoordinator.WaitingBots"/>), in LookingForGroup or General,
/// and "anyone for &lt;quest&gt;?" in General for an elite quest;</item>
/// <item>WTS for real surplus in the bags, WTB for the reagents of wanted recipes, water for a mana user without drink (Trade in a capital,
/// now and then General);</item>
/// <item>party needs of a bot grouped with a real player ("oom", "need heals", "rez pls", "inc 3", "&lt;mob&gt; on me", "brb", "ty", "ding",
/// a quest share request, "first time in &lt;zone&gt;");</item>
/// <item>the plan of the bot leading a dungeon or raid run (<see cref="PlayerbotRunMachine"/> steps);</item>
/// <item>battleground callouts from the match's real state (Warsong Gulch flags, Arathi Basin bases, Alterac Valley towers and graveyards),
/// one bot per event.</item>
/// </list>
/// Every line goes to <see cref="PlayerbotChat.TryAnnounce"/> with a <see cref="BotSpeech"/> envelope: the built-in templates
/// (<see cref="PlayerbotVoiceTemplates"/>) write it, or a configured model, the chat safety screens it, and the speech delivery says it
/// through the bot's own session within the speech limits (a real listener for anything the bot starts; per bot, per channel and zone,
/// per group). As <see cref="PlayerbotChat.Responder"/> it answers a real player's whisper to a poster: "inv" to an LFG or LFM call
/// (the player is matched into the group through <see cref="PlayerbotGroupCoordinator.Volunteer"/>) and a price question to a WTS call; and
/// one bot says "gz" to a real player's "ding" in party or guild chat.
/// </summary>
internal sealed class PlayerbotNeedsVoice(IServiceProvider services, ILogger logger)
{
    /// <summary>A fight counts as just started (a tank's "inc") for this long.</summary>
    internal const long PullWindowMs = 5_000;

    /// <summary>A buff counts for thanks from this long a duration (a long buff, not a heal over time).</summary>
    internal const int LongBuffMs = 300_000;

    /// <summary>The least time between two thanks of one bot to one caster.</summary>
    internal const long ThanksGapMs = 600_000;

    /// <summary>The bag items looked at per trade call.</summary>
    internal const int MaxItemsScanned = 150;

    private readonly Dictionary<Guid, BotState> _bots = [];
    private readonly Dictionary<int, RunState> _runs = [];
    private readonly Dictionary<(BattlegroundType Type, uint Instance), BattlegroundState> _battlegrounds = [];
    private readonly Dictionary<string, long> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<((PlayerbotGroupGoalKind, uint) Goal, Team Team), long> _holds = [];
    private int _verdictsThisPass;
    private readonly HashSet<ObjectGuid> _dings = [];
    private readonly Random _random = new();
    private PlayerbotChat? _hooked;
    private int _cursor;
    private int _budget;
    private long _prunedMs;

    /// <summary>Lines handed to the chat service so far (tests and status).</summary>
    internal long Asked { get; private set; }

    /// <summary>The generator of every random choice (tests seed it).</summary>
    internal Random Random { get; set; } = new();

    // --- the pass ------------------------------------------------------------------------------------------------------

    /// <summary>One social pass (world thread).</summary>
    internal void Run(PlayerbotSocialPass pass)
    {
        ArgumentNullException.ThrowIfNull(pass);
        if (pass.Chat is not { IsActive: true } chat)
        {
            Unhook();
            return;
        }

        Hook(chat);
        PlayerbotVoiceOptions options = chat.VoiceOptions;
        if (!options.Enabled || !chat.SpeechOptions.Enabled || pass.Bots.Count == 0) return;
        _budget = options.LinesPerPass;
        _verdictsThisPass = 0;
        if (options.BattlegroundCalls) Battlegrounds(pass, chat, options);
        if (options.RunCalls) Runs(pass, chat);
        if (options.LookingForGroup || options.QuestHelp) LookingForGroup(pass, chat, options);
        Recruits(pass, chat);
        Bots(pass, chat, options);
        Prune(pass);
    }

    /// <summary>A player's level changed (the social feature's progression event): a bot's ding is said in party at its next look.</summary>
    internal void OnLevelChanged(Player player)
    {
        if (player.Session is WorldSession { IsManaged: true } && _dings.Count < 1024) _dings.Add(player.Guid);
    }

    /// <summary>Answer heard lines from now on (<see cref="PlayerbotChat.Responder"/>).</summary>
    private void Hook(PlayerbotChat chat)
    {
        if (ReferenceEquals(_hooked, chat)) return;
        Unhook();
        if (chat.Responder is not null) return; // another responder holds it: the voice only speaks
        chat.Responder = Respond;
        _hooked = chat;
    }

    /// <summary>Stop answering heard lines (the social feature stops, or chat went off).</summary>
    internal void Unhook()
    {
        if (_hooked is { } chat && chat.Responder == Respond) chat.Responder = null;
        _hooked = null;
    }

    private bool Spend() => _budget-- > 0;

    // --- saying ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Hand one line to the chat service: <paramref name="key"/> picks the built-in lines, <paramref name="sentence"/> says what it is about
    /// (screened like any input), <paramref name="instruction"/> tells a model what to write, <paramref name="values"/> fill the topic's
    /// placeholders. The bot's own limits are peeked first, so a line that could not be said costs nothing.
    /// </summary>
    private bool Say(PlayerbotChat chat, Guid botId, Player player, PlayerbotPartyAI party, BotSpeech speech, string key, string sentence,
        string instruction, BotChatPersona? persona = null, params (string Key, string Value)[] values)
    {
        if (chat.Limiter.Peek(botId, player.Guid, speech, chat.SpeechOptions, chat.NowMs) != BotSpeechAdmission.Queued) return false;
        var map = new Dictionary<string, string>(values.Length, StringComparer.Ordinal);
        foreach ((string name, string value) in values) map[name] = value;
        bool toPlayer = !speech.TargetGuid.IsEmpty && speech.Channel == BotSpeechChannel.Whisper;
        var ask = new BotChatAsk(botId, player.Guid, persona ?? party.PersonaFor(player), toPlayer ? speech.TargetGuid : player.Guid,
            toPlayer ? speech.Target ?? player.Name : player.Name, speech.ChatType, sentence, FromMaster: false, InviteAllowed: false, SenderAccountId: 0,
            Topic: new BotChatTopic(key, instruction, map, string.Empty), Speech: speech);
        if (chat.TryAnnounce(ask) != BotChatAdmission.Queued) return false;
        Asked++;
        logger.LogDebug("Playerbot {Bot} voices {Key} on {Channel}{Where}", player.Name, key, speech.Channel,
            speech.ChannelName is { } channel ? " in " + channel : string.Empty);
        return true;
    }

    private static BotSpeech Party(string topic, BotSpeechPriority priority = BotSpeechPriority.Normal) => new(BotSpeechChannel.Party, string.Empty, topic, priority);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    // --- looking for group -------------------------------------------------------------------------------------------------

    private readonly Dictionary<Guid, PlayerbotSocialBot> _byIdScratch = [];

    /// <summary>
    /// The bots waiting for partners (<see cref="PlayerbotGroupCoordinator.WaitingBots"/>), by goal and team: the one waiting longest posts
    /// LFG (alone) or LFM with the roles still missing, at most once per <see cref="PlayerbotVoiceOptions.LookingForGroupRepeatSeconds"/>;
    /// an elite quest is also asked for in General ("anyone for &lt;quest&gt;?") once per <see cref="PlayerbotVoiceOptions.QuestHelpRepeatSeconds"/>.
    /// </summary>
    private void LookingForGroup(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotVoiceOptions options)
    {
        if (services.GetService<PlayerbotGroupCoordinator>() is not { } coordinator) return;
        Dictionary<Guid, PlayerbotSocialBot> byId = _byIdScratch;
        byId.Clear();
        var wanting = new Dictionary<((PlayerbotGroupGoalKind, uint) Key, Team Team), List<(PlayerbotSocialBot Bot, PlayerbotGroupGoal Goal, uint Since)>>();
        foreach ((Guid id, string _, PlayerbotGroupGoal goal, uint since) in coordinator.WaitingBots)
        {
            if (byId.Count == 0) foreach (PlayerbotSocialBot running in pass.Bots) byId[running.Id] = running;
            if (!byId.TryGetValue(id, out PlayerbotSocialBot? bot)) continue;
            var key = (goal.Key, bot.Player.Team);
            if (!wanting.TryGetValue(key, out var list)) wanting[key] = list = [];
            list.Add((bot, goal, since));
        }
        byId.Clear();

        foreach (var list in wanting.Values)
        {
            if (_budget <= 0) return;
            if (ChoosePoster(pass, options, list) is not { } chosen) continue;
            (PlayerbotSocialBot poster, PlayerbotGroupGoal goal, uint _) = chosen;
            Player player = poster.Player;
            if (!player.IsAlive || player.Combat.IsInCombat) continue;
            BotState state = StateOf(poster, pass.NowMs);
            int size = list.Max(w => w.Goal.Size);
            string? what = LfgSubject(pass, goal);
            if (what is null) continue;
            if (options.QuestHelp && goal.Kind == PlayerbotGroupGoalKind.Quest && pass.NowMs - state.QuestHelpMs >= options.QuestHelpRepeatSeconds * 1000L
                && Channel(pass, player, BuiltInChannels.GeneralId) is { } general)
            {
                state.QuestHelpMs = pass.NowMs;
                if (Spend() && Say(chat, poster.Id, player, poster.Party, BotSpeech.InChannel(general, string.Empty, "quest-help", BotSpeechPriority.Normal),
                        "voice.questhelp", $"Asking for help with the group quest {what}.",
                        $"Ask the zone's general chat whether anyone wants to do the elite quest {what} with you.", null, ("task", what)))
                    chat.Voice.Post(poster.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, what, MinLevel: goal.MinLevel));
                continue;
            }

            if (!options.LookingForGroup || pass.NowMs - state.LfgMs < options.LookingForGroupRepeatSeconds * 1000L) continue;
            string? channel = LfgChannel(pass, player, options);
            if (channel is null) continue;
            state.LfgMs = pass.NowMs;
            var classes = list.Select(w => w.Bot.Player.Class).ToList();
            (int tanks, int healers) = PlayerbotVoiceFacts.MissingRoles(classes, PlayerbotGroupContent.RoleNeeds(size, Options(pass)));
            string? roles = PlayerbotVoiceFacts.RoleWords(tanks, healers);
            int missing = Math.Max(1, size - list.Count);
            bool quest = goal.Kind == PlayerbotGroupGoalKind.Quest;
            string key = list.Count == 1 ? quest ? "voice.lfg.quest" : "voice.lfg.dungeon"
                : roles is null ? quest ? "voice.lfm.quest.dps" : "voice.lfm.dungeon.dps" : quest ? "voice.lfm.quest" : "voice.lfm.dungeon";
            string sentence = list.Count == 1 ? $"Looking for a group for {what}." : $"Looking for {Number(missing)} more for {what}.";
            string instruction = list.Count == 1
                ? $"Post a short looking-for-group line for {what}, with your level and class."
                : $"Post a short looking-for-more line for {what}{(roles is null ? string.Empty : $", needing {roles}")}.";
            var values = new List<(string, string)> { ("dungeon", what), ("task", what), ("count", Number(missing)) };
            if (roles is not null) values.Add(("roles", roles));
            if (!Spend()) return;
            if (Say(chat, poster.Id, player, poster.Party, BotSpeech.InChannel(channel, string.Empty, list.Count == 1 ? "lfg" : "lfm", BotSpeechPriority.Normal),
                    key, sentence, instruction, null, [.. values]))
                chat.Voice.Post(poster.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.LookingForGroup, chat.NowMs, what, MinLevel: goal.MinLevel));
        }
    }

    /// <summary>What an LFG line for <paramref name="goal"/> names: the quest's title, else the dungeon as players call it; null when there is no name.</summary>
    private string? LfgSubject(PlayerbotSocialPass pass, PlayerbotGroupGoal goal)
        => goal.Kind == PlayerbotGroupGoalKind.Quest
            ? QuestTitle(pass, goal.QuestId)
            : PlayerbotVoiceFacts.DungeonName(goal.MapId, WorldMaps.Of(pass.World).Registry.Find(goal.MapId)?.Name, Random);

    /// <summary>
    /// The waiting bot that posts for the goal of <paramref name="list"/> (bots of one goal and team): the one waiting longest. Under
    /// <see cref="PlayerbotVoiceOptions.LfgNearDungeonOnly"/> the first of them in that order that is alive, out of combat and near enough to
    /// the goal's place (<c>PlayerbotLfgLocality.ForPlace</c>, a warlock of the list with two helpers counting as a summon); null when none is, in which
    /// case the line is not posted and the holding is logged at Debug once per <see cref="PlayerbotVoiceOptions.LookingForGroupRepeatSeconds"/>.
    /// </summary>
    private (PlayerbotSocialBot Bot, PlayerbotGroupGoal Goal, uint Since)? ChoosePoster(PlayerbotSocialPass pass, PlayerbotVoiceOptions options,
        List<(PlayerbotSocialBot Bot, PlayerbotGroupGoal Goal, uint Since)> list)
    {
        var ordered = list.OrderBy(w => w.Since).ToList();
        if (!options.LfgNearDungeonOnly) return ordered[0];

        var waiters = new (bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict)[ordered.Count];
        for (int i = 0; i < ordered.Count; i++)
        {
            var waiting = ordered[i];
            Player candidate = waiting.Bot.Player;
            waiters[i] = (candidate.IsAlive && !candidate.Combat.IsInCombat, () => LocalityOf(pass, options, waiting, list));
        }

        PosterChoice choice = ChoosePosterIndex(waiters);
        if (choice.Index is { } index) return ordered[index];
        // Verdicts left for the next pass (the per-pass cap) are not a holding: nothing is logged and the goal is looked at again.
        if (!choice.Deferred && choice.HeldIndex is { } heldIndex)
        {
            var held = ordered[heldIndex];
            var holdKey = (held.Goal.Key, held.Bot.Player.Team);
            if (!_holds.TryGetValue(holdKey, out long at) || pass.NowMs - at >= options.LookingForGroupRepeatSeconds * 1000L)
            {
                _holds[holdKey] = pass.NowMs;
                logger.LogDebug("Playerbot {Bot} holds its LFG for {What}: {Trace}", held.Bot.Player.Name, LfgSubject(pass, held.Goal) ?? held.Goal.ToString(), choice.HeldVerdict.Trace);
            }
        }

        return null;
    }

    /// <summary>The outcome of <see cref="ChoosePosterIndex"/>.</summary>
    /// <param name="Index">The waiter that posts, or null.</param>
    /// <param name="Deferred">At least one eligible waiter's verdict was not available yet (null), so a null <paramref name="Index"/> is not final.</param>
    /// <param name="HeldIndex">The first eligible waiter whose verdict refused (the one a holding is logged for), or null.</param>
    /// <param name="HeldVerdict">The refusing verdict of <paramref name="HeldIndex"/>.</param>
    internal readonly record struct PosterChoice(int? Index, bool Deferred, int? HeldIndex, PlayerbotLfgLocalityVerdict HeldVerdict);

    /// <summary>
    /// Who posts the LFG of a goal: the first waiter, in waiting order, that is eligible (alive, out of combat) and whose locality verdict allows
    /// advertising. A verdict is asked lazily and only for eligible waiters up to the one that is allowed; a null verdict means it cannot be
    /// worked out now (the per-pass cap), which marks the choice <see cref="PosterChoice.Deferred"/> and moves on to the next waiter. Pure.
    /// </summary>
    internal static PosterChoice ChoosePosterIndex(IReadOnlyList<(bool Eligible, Func<PlayerbotLfgLocalityVerdict?> Verdict)> waitersInSinceOrder)
    {
        ArgumentNullException.ThrowIfNull(waitersInSinceOrder);
        bool deferred = false;
        int? heldIndex = null;
        PlayerbotLfgLocalityVerdict heldVerdict = default;
        for (int i = 0; i < waitersInSinceOrder.Count; i++)
        {
            if (!waitersInSinceOrder[i].Eligible) continue;
            if (waitersInSinceOrder[i].Verdict() is not { } verdict)
            {
                deferred = true;
                continue;
            }

            if (verdict.MayAdvertise) return new PosterChoice(i, deferred, heldIndex, heldVerdict);
            if (heldIndex is null)
            {
                heldIndex = i;
                heldVerdict = verdict;
            }
        }

        return new PosterChoice(null, deferred, heldIndex, heldVerdict);
    }

    /// <summary>The most locality verdicts worked out in one social pass, across all goals (the planner is the cost); cached verdicts are free.</summary>
    internal const int MaxLocalityVerdictsPerPass = 8;

    /// <summary>How long a locality verdict is kept when the bot's or the place's zone was unknown (10 s).</summary>
    internal const long UnknownZoneVerdictMs = 10_000;

    /// <summary>The bot's locality verdict for the goal's place, worked out at most once per <see cref="PlayerbotVoiceOptions.LookingForGroupRepeatSeconds"/> and kept in its state.</summary>
    private PlayerbotLfgLocalityVerdict? LocalityOf(PlayerbotSocialPass pass, PlayerbotVoiceOptions options,
        (PlayerbotSocialBot Bot, PlayerbotGroupGoal Goal, uint Since) waiting, List<(PlayerbotSocialBot Bot, PlayerbotGroupGoal Goal, uint Since)> list)
    {
        BotState state = StateOf(waiting.Bot, pass.NowMs);
        var key = waiting.Goal.Key;
        if (state.HasLocality && state.LocalityKey == key && pass.NowMs - state.LocalityMs < state.LocalityTtlMs) return state.Locality;

        if (_verdictsThisPass >= MaxLocalityVerdictsPerPass) return null; // retried on a later pass
        _verdictsThisPass++;
        PlayerbotGroupGoal goal = waiting.Goal;
        Player player = waiting.Bot.Player;
        state.Locality = PlayerbotLfgLocality.ForPlace(waiting.Bot.Session, player, pass.Bot, goal.MeetingMap, goal.Meeting,
            () => PlayerbotLfgLocality.SummonAvailable(list.Select(w => (w.Bot.Player, w.Bot.Session)), player, goal.MeetingMap, goal.Meeting));
        state.LocalityKey = key;
        state.LocalityMs = pass.NowMs;
        // An unknown zone (the terrain tile is not loaded yet) is a verdict to ask again soon, not for the whole repeat interval.
        bool zoneUnknown = player.MapId == goal.MeetingMap && (player.ZoneId == 0 || PlayerbotLfgLocality.ZoneAt(player, goal.MeetingMap, goal.Meeting) == 0);
        state.LocalityTtlMs = zoneUnknown ? UnknownZoneVerdictMs : options.LookingForGroupRepeatSeconds * 1000L;
        state.HasLocality = true;
        return state.Locality;
    }

    /// <summary>LookingForGroup, or (outside a capital, <see cref="PlayerbotVoiceOptions.GeneralSharePercent"/> of the time, or without it) General.</summary>
    private string? LfgChannel(PlayerbotSocialPass pass, Player player, PlayerbotVoiceOptions options)
    {
        string? lfg = Channel(pass, player, BuiltInChannels.LookingForGroupId);
        string? general = Channel(pass, player, BuiltInChannels.GeneralId);
        bool capital = Channel(pass, player, BuiltInChannels.TradeId) is not null;
        if (lfg is null) return general;
        return general is not null && !capital && Random.Next(100) < options.GeneralSharePercent ? general : lfg;
    }

    /// <summary>The name of the zone channel of <paramref name="channelId"/> the bot is in (null when it is in none).</summary>
    private static string? Channel(PlayerbotSocialPass pass, Player player, uint channelId)
    {
        foreach (Channel channel in pass.Social.Channels.JoinedBy(player))
            if (channel.IsConstant && channel.ChannelId == channelId) return channel.Name;
        return null;
    }

    private static string? QuestTitle(PlayerbotSocialPass pass, uint questId)
        => questId == 0 ? null : pass.Quests?.Quests.Get(questId)?.Title is { Length: > 0 } title ? title : null;

    private static PlayerbotGroupOptions Options(PlayerbotSocialPass pass) => pass.Bot.Groups;

    // --- runs ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bot leading a dungeon or raid run says the plan in party (raid) chat as the run moves on: "everyone in?" on entering,
    /// "pulling &lt;boss&gt;" when the boss fight starts, "&lt;boss&gt; down", "wait for mana" when a member stops to drink between pulls,
    /// "wipe, run back" after a wipe and "gg" at the end. The first look at a run only records it.
    /// </summary>
    private void Runs(PlayerbotSocialPass pass, PlayerbotChat chat)
    {
        if (services.GetService<PlayerbotGroupCoordinator>() is not { } coordinator) return;
        foreach (PlayerbotGroupCoordinator.BotGroup group in coordinator.Groups)
        {
            if (group.Run is not { } run) continue;
            bool resting = group.AnyResting(pass.World);
            int killed = run.Killed;
            string? boss = run.CurrentBoss?.Name;
            if (!_runs.TryGetValue(group.Id, out RunState? seen))
            {
                _runs[group.Id] = new RunState { Step = run.Step, Killed = killed, Resting = resting, Boss = boss, SeenMs = pass.NowMs };
                continue;
            }

            seen.SeenMs = pass.NowMs;
            (string Key, string Sentence, string? Boss)? call = null;
            if (run.Step != seen.Step)
            {
                call = run.Step switch
                {
                    PlayerbotRunStep.Enter => ("voice.run.enter", $"Going into {run.Plan.MapName}.", null),
                    PlayerbotRunStep.Boss when boss is not null => ("voice.run.pull", $"Pulling {boss}.", boss),
                    PlayerbotRunStep.Wiped => ("voice.run.wipe", "The group wiped and runs back.", null),
                    PlayerbotRunStep.Leave or PlayerbotRunStep.Complete when seen.Step is not PlayerbotRunStep.Leave => ("voice.run.done", $"The run of {run.Plan.MapName} is done.", null),
                    _ => null,
                };
            }

            if (call is null && killed > seen.Killed && seen.Boss is { } fallen) call = ("voice.run.bossdown", $"{fallen} is dead.", fallen);
            if (call is null && resting && !seen.Resting && run.Step == PlayerbotRunStep.Clear) call = ("voice.run.mana", "The group waits for mana.", null);
            seen.Step = run.Step;
            seen.Killed = killed;
            seen.Resting = resting;
            seen.Boss = boss;
            if (call is not { } said || _budget <= 0 || !pass.BotIds.Contains(group.LeaderBotId)) continue;
            PlayerbotSocialBot? leader = pass.Bots.FirstOrDefault(b => b.Id == group.LeaderBotId);
            if (leader is null || !Spend()) continue;
            Say(chat, leader.Id, leader.Player, leader.Party, Party("run"), said.Key, said.Sentence,
                $"You lead this dungeon group. Tell the group, briefly: {said.Sentence}", null, ("boss", said.Boss ?? string.Empty), ("dungeon", run.Plan.MapName));
        }
    }

    // --- battlegrounds --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The callouts of each running battleground with bots in it, from its real state compared with the last pass: one bot of the team
    /// concerned says each event in battleground chat; the same callout is not said again within
    /// <see cref="PlayerbotVoiceOptions.BattlegroundCallSeconds"/>.
    /// </summary>
    private void Battlegrounds(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotVoiceOptions options)
    {
        if (services.GetService<BattlegroundManager>() is not { } manager) return;
        foreach (Battleground match in manager.RunningBattlegrounds)
        {
            if (_budget <= 0) return;
            if (match.Status != BattlegroundStatus.InProgress) continue;
            var key = (match.Type, match.InstanceId);
            bool first = !_battlegrounds.TryGetValue(key, out BattlegroundState? state);
            if (first) _battlegrounds[key] = state = new BattlegroundState();
            state!.SeenMs = pass.NowMs;
            var calls = new List<(Team Team, string Key, string Event, string? Node, string? Where)>();
            switch (match)
            {
                case WarsongGulch wsg: Warsong(pass, wsg, state, first, calls); break;
                case ArathiBasin ab: Arathi(ab, state, first, calls); break;
                case AlteracValley av: Alterac(av, state, first, calls); break;
            }

            foreach ((Team team, string callKey, string eventKey, string? node, string? where) in calls)
            {
                if (_budget <= 0) return;
                string coalesce = string.Create(CultureInfo.InvariantCulture, $"{match.Type}:{match.InstanceId}:{(int)team}:{eventKey}");
                if (_calls.TryGetValue(coalesce, out long at) && pass.NowMs - at < options.BattlegroundCallSeconds * 1000L) continue;
                if (Speaker(pass, match, team, state) is not { } speaker) continue;
                _calls[coalesce] = pass.NowMs;
                if (!Spend()) return;
                string sentence = callKey switch
                {
                    "voice.bg.flagtaken" => "The enemy took our flag.",
                    "voice.bg.efc" => $"The enemy flag carrier is {where}.",
                    "voice.bg.flagback" => "Our flag was returned.",
                    _ => $"Battleground news about {node}: {callKey[(callKey.LastIndexOf('.') + 1)..]}.",
                };
                Say(chat, speaker.Id, speaker.Player, speaker.Party, new BotSpeech(BotSpeechChannel.Battleground, string.Empty, "bg-call"), callKey, sentence,
                    $"Call it out to your battleground team, very short: {sentence}", null, ("node", node ?? string.Empty), ("where", where ?? string.Empty));
            }
        }
    }

    /// <summary>A living bot of <paramref name="team"/> in the match to say a callout, the team's bots taken in turn.</summary>
    private static PlayerbotSocialBot? Speaker(PlayerbotSocialPass pass, Battleground match, Team team, BattlegroundState state)
    {
        IReadOnlyList<ObjectGuid> members = match.TeamMembers(team);
        int count = members.Count;
        for (int i = 0; i < count; i++)
        {
            ObjectGuid guid = members[(state.NextSpeaker + i) % count];
            if (!pass.ByCharacter.TryGetValue(guid.Low, out PlayerbotSocialBot? bot) || bot.Player.Guid != guid || !bot.Player.IsAlive) continue;
            state.NextSpeaker = (state.NextSpeaker + i + 1) % count;
            return bot;
        }

        return null;
    }

    private static readonly Team[] s_teams = [Team.Alliance, Team.Horde];

    private static Team Other(Team team) => team == Team.Alliance ? Team.Horde : Team.Alliance;

    private static void Warsong(PlayerbotSocialPass pass, WarsongGulch wsg, BattlegroundState state, bool first, List<(Team, string, string, string?, string?)> calls)
    {
        foreach (Team team in s_teams)
        {
            int index = BattlegroundConstants.TeamIndex(team);
            WsgFlagState flag = wsg.FlagState(team);
            WsgFlagState before = state.Flags[index];
            state.Flags[index] = flag;
            if (first) continue;
            if (before == WsgFlagState.OnBase && flag == WsgFlagState.OnPlayer) calls.Add((team, "voice.bg.flagtaken", "flag-taken", null, null));
            if (before is WsgFlagState.OnGround or WsgFlagState.OnPlayer && flag == WsgFlagState.OnBase) calls.Add((team, "voice.bg.flagback", "flag-back", null, null));
            if (flag == WsgFlagState.OnPlayer && pass.World.FindOnlinePlayer(wsg.FlagPicker(team)) is { } carrier)
            {
                BattlegroundStartLocation ours = team == Team.Alliance ? wsg.Template.AllianceStart : wsg.Template.HordeStart;
                BattlegroundStartLocation theirs = team == Team.Alliance ? wsg.Template.HordeStart : wsg.Template.AllianceStart;
                string where = PlayerbotVoiceFacts.CarrierWhere(new Vector3(carrier.X, carrier.Y, carrier.Z), new Vector3(ours.X, ours.Y, ours.Z),
                    new Vector3(theirs.X, theirs.Y, theirs.Z));
                calls.Add((team, "voice.bg.efc", "efc:" + where, null, where));
            }
        }
    }

    private void Arathi(ArathiBasin ab, BattlegroundState state, bool first, List<(Team, string, string, string?, string?)> calls)
    {
        for (int node = 0; node < ArathiBasin.NodeCount; node++)
        {
            byte status = ab.NodeStatus(node);
            byte before = state.Nodes[node];
            Team? owner = state.Owners[node];
            state.Nodes[node] = status;
            if (status == ArathiBasin.StatusAllianceOccupied) state.Owners[node] = Team.Alliance;
            else if (status == ArathiBasin.StatusHordeOccupied) state.Owners[node] = Team.Horde;
            if (first) continue;
            foreach (Team team in s_teams)
                if (PlayerbotVoiceFacts.AbCall(before, status, team, owner == team) is { } key && PlayerbotVoiceFacts.AbNode(node, Random) is { } name)
                    calls.Add((team, key, string.Create(CultureInfo.InvariantCulture, $"ab:{node}:{key}"), name, null));
        }
    }

    private void Alterac(AlteracValley av, BattlegroundState state, bool first, List<(Team, string, string, string?, string?)> calls)
    {
        for (int node = 0; node < AlteracValley.NodeCount; node++)
        {
            int owner = av.NodeOwner(node), nodeState = av.NodeState(node);
            int ownerBefore = state.AvOwners[node], stateBefore = state.AvStates[node];
            state.AvOwners[node] = owner;
            state.AvStates[node] = nodeState;
            if (first) continue;
            foreach (Team team in s_teams)
                if (PlayerbotVoiceFacts.AvCall(ownerBefore, stateBefore, owner, nodeState, av.IsTower(node), BattlegroundConstants.TeamIndex(team)) is { } key
                    && PlayerbotVoiceFacts.AvNode(node, Random) is { } name)
                    calls.Add((team, key, string.Create(CultureInfo.InvariantCulture, $"av:{node}:{key}"), name, null));
        }
    }

    // --- one bot at a time -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The per-bot topics for at most <see cref="PlayerbotVoiceOptions.BotsPerPass"/> bots: every bot grouped with a real player (their
    /// fights move fast) up to that number, then the others in turn.
    /// </summary>
    private void Bots(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotVoiceOptions options)
    {
        IReadOnlyList<PlayerbotSocialBot> bots = pass.Bots;
        var withReal = new Dictionary<uint, bool>();
        var hot = new List<(PlayerbotSocialBot Bot, Group Group)>();
        var hotIds = new HashSet<Guid>();
        if (options.GroupCalls)
            foreach (PlayerbotSocialBot bot in bots)
            {
                if (hot.Count >= options.BotsPerPass) break;
                if (pass.Social.Groups.GetGroup(bot.Player.Guid) is not { IsBattlegroundGroup: false } group) continue;
                if (!withReal.TryGetValue(group.Id, out bool real)) withReal[group.Id] = real = HasRealMember(pass, group, bot.Player);
                if (!real) continue;
                hot.Add((bot, group));
                hotIds.Add(bot.Id);
            }

        foreach ((PlayerbotSocialBot bot, Group group) in hot) Visit(pass, chat, options, bot, group);
        int cold = options.BotsPerPass - hot.Count;
        for (int i = 0; i < bots.Count && cold > 0; i++)
        {
            PlayerbotSocialBot bot = bots[(_cursor + i) % bots.Count];
            if (hotIds.Contains(bot.Id)) continue;
            cold--;
            Visit(pass, chat, options, bot, null);
        }

        _cursor = bots.Count == 0 ? 0 : (_cursor + Math.Max(1, options.BotsPerPass - hot.Count)) % bots.Count;
    }

    private static bool HasRealMember(PlayerbotSocialPass pass, Group group, Player self)
    {
        foreach (GroupMemberSlot slot in group.Members)
            if (slot.Guid != self.Guid && pass.World.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } }) return true;
        return false;
    }

    /// <summary>One bot's topics; <paramref name="group"/> is its group when a real player is in it (party talk has a listener).</summary>
    private void Visit(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotVoiceOptions options, PlayerbotSocialBot bot, Group? group)
    {
        Player player = bot.Player;
        BotState state = StateOf(bot, pass.NowMs);
        bool fresh = state.Fresh;
        state.Fresh = false;
        if (options.GroupCalls)
        {
            NewZone(pass, chat, bot, state, group, fresh);
            if (group is not null)
            {
                Life(pass, chat, bot, state, group);
                Fight(pass, chat, bot, state, group);
                Stops(pass, chat, bot, state);
                Thanks(pass, chat, bot, state, group, fresh);
                Ding(chat, bot);
                AskShare(pass, chat, bot, state);
            }
            else
            {
                _dings.Remove(player.Guid);
                state.ResetFight();
            }
        }

        if (options.Trade && group is null && !fresh) Trade(pass, chat, options, bot, state);
    }

    private BotState StateOf(PlayerbotSocialBot bot, long nowMs)
    {
        if (!_bots.TryGetValue(bot.Id, out BotState? state) || !ReferenceEquals(state.Player, bot.Player))
        {
            _bots[bot.Id] = state = new BotState { Player = bot.Player, Zone = bot.Player.ZoneId, WasAlive = bot.Player.IsAlive };
            // Spread the first calls: a fleet logging in at once does not post at once.
            state.TradeMs = nowMs - Random.NextInt64(0, 600_000);
            state.LfgMs = nowMs - Random.NextInt64(60_000, 180_000);
            state.QuestHelpMs = nowMs - Random.NextInt64(0, 600_000);
        }

        state.SeenMs = nowMs;
        return state;
    }

    /// <summary>"first time in &lt;zone&gt;": a zone none of whose areas the bot had explored when it was first seen this session.</summary>
    private void NewZone(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state, Group? group, bool fresh)
    {
        Player player = bot.Player;
        uint zone = player.ZoneId;
        if (fresh || state.Explored is null)
        {
            state.Explored = ExploredZonesFields.Read(player);
            state.Zone = zone;
            return;
        }

        if (zone == state.Zone || zone == 0) return;
        state.Zone = zone;
        if (player.Map?.Template is { Instanceable: true }) return;
        Game.Maps.Terrain.AreaTable areas = WorldMaps.Of(pass.World).Areas;
        if (areas.GetById(zone) is not { } template) return;
        bool known = false;
        foreach (Kernel.WorldData.AreaTemplate area in areas.All)
        {
            if (area.Entry != zone && area.ZoneId != zone) continue;
            if (ExploredZones.IsExplored(state.Explored, area.ExploreFlag)) { known = true; break; }
        }

        ExploredZones.Mark(state.Explored, template.ExploreFlag);
        if (known || _budget <= 0 || !player.IsAlive || player.Combat.IsInCombat) return;
        BotSpeech speech = group is not null ? Party("new-zone", BotSpeechPriority.Low) : new BotSpeech(BotSpeechChannel.Say, string.Empty, "new-zone", BotSpeechPriority.Low);
        if (Spend())
            Say(chat, bot.Id, player, bot.Party, speech, "voice.newzone", $"You have just arrived in {template.Name} for the first time.",
                $"Remark that this is your first time in {template.Name}.", null, ("newzone", template.Name));
    }

    /// <summary>"rez pls" once per death while a resurrecting class is alive in the group; "ty &lt;caster&gt;" once alive after a resurrection.</summary>
    private void Life(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state, Group group)
    {
        Player player = bot.Player;
        if (!player.IsAlive)
        {
            state.WasAlive = false;
            if (state.RezAsked || _budget <= 0) return;
            bool resurrecter = false;
            foreach (GroupMemberSlot slot in group.Members)
                if (slot.Guid != player.Guid && pass.World.FindOnlinePlayer(slot.Guid) is { IsAlive: true } member && ReferenceEquals(member.Map, player.Map)
                    && (member.Class is Class.Priest or Class.Paladin or Class.Shaman || member.Class == Class.Druid && member.Level >= 20)) { resurrecter = true; break; }
            if (!resurrecter) return;
            state.RezAsked = true;
            if (Spend())
                Say(chat, bot.Id, player, bot.Party, Party("rez", BotSpeechPriority.High), "voice.rez", "You died and need a resurrection.",
                    "Ask your group for a resurrection.");
            return;
        }

        state.RezAsked = false;
        state.WasAlive = true;
        if (!chat.Voice.OwesThanks(bot.Id) || !chat.Voice.TakeResurrection(bot.Id, chat.NowMs, out ObjectGuid caster)) return;
        if (pass.World.FindOnlinePlayer(caster) is not { } giver || _budget <= 0 || !Spend()) return;
        Say(chat, bot.Id, player, bot.Party, Party("thanks"), "voice.ty.rez", $"{giver.Name} resurrected you.", $"Thank {giver.Name} for the resurrection.",
            null, ("who", giver.Name));
    }

    /// <summary>"oom", "need heals", a tank's "inc &lt;n&gt;" and "&lt;mob&gt; on me", each once per fight.</summary>
    private void Fight(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state, Group group)
    {
        Player player = bot.Player;
        if (!player.IsAlive || !player.Combat.IsInCombat)
        {
            state.ResetFight();
            return;
        }

        if (state.FightSinceMs == 0) state.FightSinceMs = pass.NowMs;
        bool tank = IsTank(bot);
        if (!state.OomSaid && player.PowerType == PowerType.Mana)
        {
            uint max = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
            if (max > 0 && SpellSystem.GetPower(player, PowerType.Mana) * 10UL < max)
            {
                state.OomSaid = true;
                if (Spend()) Say(chat, bot.Id, player, bot.Party, Party("oom", BotSpeechPriority.High), "voice.oom", "You are out of mana in a fight.", "Tell your group you are out of mana.");
                return;
            }
        }

        if (!state.HealsSaid && player.HealthPercent < 30f && HasOther(pass, group, player, c => PlayerbotGroupContent.CanHeal(c)))
        {
            state.HealsSaid = true;
            if (Spend()) Say(chat, bot.Id, player, bot.Party, Party("heals", BotSpeechPriority.High), "voice.heals", "You are low on health in a fight.", "Ask your healer for heals.");
            return;
        }

        int creatures = 0;
        Creature? onMe = null;
        foreach (Unit attacker in player.Combat.Attackers)
        {
            if (attacker is not Creature { IsAlive: true } creature) continue;
            creatures++;
            if (onMe is null && ReferenceEquals(creature.Combat.Victim, player) && !state.AggroSaid.Contains(creature.Guid.Value)) onMe = creature;
        }

        if (tank && !state.IncSaid && pass.NowMs - state.FightSinceMs <= PullWindowMs && creatures >= 2)
        {
            state.IncSaid = true;
            if (Spend()) Say(chat, bot.Id, player, bot.Party, Party("inc", BotSpeechPriority.High), "voice.inc", $"You pulled {Number(creatures)} enemies.",
                $"Warn your group that {Number(creatures)} enemies are coming.", null, ("count", Number(creatures)));
            return;
        }

        if (!tank && onMe is not null && state.AggroSaid.Count < 2 && HasOther(pass, group, player, c => PlayerbotGroupContent.CanTank(c)))
        {
            state.AggroSaid.Add(onMe.Guid.Value);
            string mob = onMe.Template.Name;
            if (!string.IsNullOrWhiteSpace(mob) && Spend())
                Say(chat, bot.Id, player, bot.Party, Party("aggro", BotSpeechPriority.High), "voice.aggro", $"{mob} is attacking you.",
                    $"Tell your tank that {mob} is attacking you.", null, ("mob", mob));
        }
    }

    /// <summary>The bot tanks: its bot group gave it the tank's job, or (in a player's group) it is the only class there that can tank.</summary>
    private bool IsTank(PlayerbotSocialBot bot)
    {
        if (services.GetService<PlayerbotGroupCoordinator>()?.GroupOf(bot.Id)?.Find(bot.Id) is { } member) return member.Role == PlayerbotGroupRole.Tank;
        return false;
    }

    private static bool HasOther(PlayerbotSocialPass pass, Group group, Player self, Func<Class, bool> can)
    {
        foreach (GroupMemberSlot slot in group.Members)
            if (slot.Guid != self.Guid && pass.World.FindOnlinePlayer(slot.Guid) is { IsAlive: true } member && can(member.Class)) return true;
        return false;
    }

    /// <summary>"brb": the bot's brain stops it to eat or drink, vendor or train, out of a fight (once per stop).</summary>
    private void Stops(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state)
    {
        if (bot.GroupDriven || services.GetService<ManagedPlayerbotFeature>()?.FindBrain(bot.Id) is not { } brain) return;
        PlayerbotGoalKind goal = brain.Goal;
        PlayerbotGoalKind? before = state.Goal;
        state.Goal = goal;
        if (before is null || before == goal || bot.Player.Combat.IsInCombat || !bot.Player.IsAlive || _budget <= 0) return;
        Player player = bot.Player;
        string? key = goal switch
        {
            PlayerbotGoalKind.Rest => player.PowerType == PowerType.Mana ? "voice.brb.drink" : "voice.brb.eat",
            PlayerbotGoalKind.Vendor => "voice.brb.vendor",
            PlayerbotGoalKind.Train => "voice.brb.train",
            _ => null,
        };
        if (key is null || !Spend()) return;
        Say(chat, bot.Id, player, bot.Party, Party("brb"), key, $"You stop for a moment ({goal.ToString().ToLowerInvariant()}).",
            "Tell your group you will be back in a moment.");
    }

    /// <summary>"ty &lt;caster&gt;" for a long buff a group member put on the bot (once per caster per <see cref="ThanksGapMs"/>).</summary>
    private void Thanks(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state, Group group, bool fresh)
    {
        Player player = bot.Player;
        if (bot.Session.Services.GetService<SpellFeature>()?.System is not { } spells) return;
        var now = new HashSet<(ulong Caster, uint Spell)>();
        Player? giver = null;
        foreach (SpellAuraHolder holder in spells.GetAuras(player))
        {
            if (holder.IsRemoved || !holder.IsPositive || holder.Spell.IsPassive || holder.AreaParent is not null || holder.CasterGuid == player.Guid
                || holder.MaxDuration < LongBuffMs || !group.Members.Any(m => m.Guid == holder.CasterGuid)) continue;
            var key = (holder.CasterGuid.Value, holder.Spell.Id);
            now.Add(key);
            if (!fresh && state.Buffs is not null && !state.Buffs.Contains(key) && giver is null
                && (!state.Thanked.TryGetValue(holder.CasterGuid.Value, out long at) || pass.NowMs - at >= ThanksGapMs))
                giver = pass.World.FindOnlinePlayer(holder.CasterGuid);
        }

        bool seeded = state.Buffs is not null;
        state.Buffs = now;
        if (!seeded || giver is null || player.Combat.IsInCombat || _budget <= 0 || !Spend()) return;
        state.Thanked[giver.Guid.Value] = pass.NowMs;
        Say(chat, bot.Id, player, bot.Party, Party("thanks"), "voice.ty", $"{giver.Name} buffed you.", $"Thank {giver.Name} for the buff.", null, ("who", giver.Name));
    }

    /// <summary>"ding" in party after a level up (the progression event noted it).</summary>
    private void Ding(PlayerbotChat chat, PlayerbotSocialBot bot)
    {
        if (!_dings.Remove(bot.Player.Guid) || _budget <= 0 || !Spend()) return;
        string level = Number(bot.Player.Level);
        Say(chat, bot.Id, bot.Player, bot.Party, Party("ding"), "voice.ding.party", $"You just reached level {level}.", $"Tell your group you just reached level {level}.",
            null, ("newlevel", level));
    }

    /// <summary>In a bot group on a quest the bot does not have yet (and could take): ask the group to share it, once per group.</summary>
    private void AskShare(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotSocialBot bot, BotState state)
    {
        if (services.GetService<PlayerbotGroupCoordinator>()?.GroupOf(bot.Id) is not { Run: null } group || group.Goal.QuestId == 0 || state.ShareAsked == group.Id) return;
        if (pass.Quests is not { } quests || quests.StateOf(bot.Player) is not { Loaded: true } log) return;
        uint quest = group.Goal.QuestId;
        if (log.Quests.GetStatus(quest) != QuestStatus.None || quests.CanTakeQuest(bot.Player, quest) != true || QuestTitle(pass, quest) is not { } title) return;
        state.ShareAsked = group.Id;
        if (_budget > 0 && Spend())
            Say(chat, bot.Id, bot.Player, bot.Party, Party("share"), "voice.share", $"You do not have the group's quest {title}.", $"Ask your group to share the quest {title}.",
                null, ("task", title));
    }

    // --- trade -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One trade call per <see cref="PlayerbotVoiceOptions.TradeRepeatSeconds"/>: in Trade in a capital (or General, now and then), else
    /// only now and then in General: WTS for the best surplus item, otherwise WTB for a missing reagent, otherwise water for a mana user
    /// without anything to drink.
    /// </summary>
    private void Trade(PlayerbotSocialPass pass, PlayerbotChat chat, PlayerbotVoiceOptions options, PlayerbotSocialBot bot, BotState state)
    {
        Player player = bot.Player;
        if (_budget <= 0 || pass.NowMs - state.TradeMs < options.TradeRepeatSeconds * 1000L) return;
        if (!player.IsAlive || player.Combat.IsInCombat || player.Map?.Template is { Instanceable: true }) return;
        state.TradeMs = pass.NowMs;
        string? trade = Channel(pass, player, BuiltInChannels.TradeId);
        string? general = Channel(pass, player, BuiltInChannels.GeneralId);
        bool generalTurn = Random.Next(100) < options.GeneralSharePercent;
        string? channel = trade is not null ? generalTurn && general is not null ? general : trade : generalTurn ? general : null;
        if (channel is null) return;
        SpellFeature? spells = bot.Session.Services.GetService<SpellFeature>();
        if (Selling(player, spells, options) is { } offer)
        {
            if (!Spend()) return;
            bool stack = offer.Count > 1;
            string key = stack ? "voice.wts.stack" : "voice.wts";
            if (Say(chat, bot.Id, player, bot.Party, BotSpeech.InChannel(channel, string.Empty, "wts"), key,
                    $"Selling {offer.Name}{(stack ? $" x{Number(offer.Count)}" : string.Empty)} for {offer.Price}{(stack ? " each" : string.Empty)}.",
                    $"Post a short WTS line offering {offer.Name}{(stack ? $" (a stack of {Number(offer.Count)})" : string.Empty)} for {offer.Price}{(stack ? " each" : string.Empty)}.",
                    null, ("item", offer.Name), ("price", offer.Price), ("count", Number(offer.Count))))
                chat.Voice.Post(bot.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.Selling, chat.NowMs, offer.Name, offer.Entry, offer.Price, offer.Count));
            return;
        }

        if (Buying(player, spells) is { } want)
        {
            if (!Spend()) return;
            if (Say(chat, bot.Id, player, bot.Party, BotSpeech.InChannel(channel, string.Empty, "wtb"), "voice.wtb",
                    $"Buying {want.Name} x{Number(want.Count)} for a recipe.", $"Post a short WTB line for {Number(want.Count)} {want.Name}.", null,
                    ("item", want.Name), ("count", Number(want.Count))))
                chat.Voice.Post(bot.Id, new PlayerbotVoicePost(PlayerbotVoicePostKind.Buying, chat.NowMs, want.Name, want.Entry, Count: (int)want.Count));
            return;
        }

        if (player.PowerType == PowerType.Mana && player.Level >= 5 && player.Class != Class.Mage && spells?.System.Store is { } store
            && PlayerbotConsumables.FindBestDrink(player, store) is null && Spend())
            Say(chat, bot.Id, player, bot.Party, BotSpeech.InChannel(channel, string.Empty, "water"), "voice.water.channel", "You have nothing to drink.",
                "Ask in chat whether a mage has water to spare.");
    }

    /// <summary>The bot's best item to offer (<see cref="PlayerbotVoiceFacts.Offer"/>), priced, or null.</summary>
    private (string Name, uint Entry, string Price, int Count)? Selling(Player player, SpellFeature? spells, PlayerbotVoiceOptions options)
    {
        PlayerbotVendorSupplyNeeds? supply = null;
        (Item Item, PlayerbotVoiceFacts.TradeOffer Offer, long Value)? best = null;
        int scanned = 0;
        foreach (Item item in player.Inventory.AllItems)
        {
            if (++scanned > MaxItemsScanned) break;
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot) || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                || item.IsBag || item.Count == 0) continue;
            ItemTemplate template = item.Template;
            bool upgrade = template.Class is 2 or 4 && PlayerbotGearScoreSeam.Gain(player, template) is > 0f;
            PlayerbotVoiceFacts.TradeOffer offer = PlayerbotVoiceFacts.Offer(template, item.Count, item.IsSoulBound, upgrade, options.MinSellCopper);
            if (offer == PlayerbotVoiceFacts.TradeOffer.None) continue;
            if (offer == PlayerbotVoiceFacts.TradeOffer.Stack)
            {
                // Trade goods the bot's own recipes call for are kept.
                if (supply is null && spells is { } feature)
                {
                    supply = new PlayerbotVendorSupplyNeeds();
                    supply.Refresh(player, feature.System.Store, feature.Spellbook);
                }

                if (supply?.Needs(player, template.Entry) == true) continue;
            }

            long value = (long)template.SellPrice * item.Count;
            if (best is null || value > best.Value.Value) best = (item, offer, value);
        }

        if (best is not { } chosen) return null;
        ItemTemplate t = chosen.Item.Template;
        bool stack = chosen.Offer == PlayerbotVoiceFacts.TradeOffer.Stack;
        string price = PlayerbotVoiceFacts.Price(PlayerbotVoiceFacts.AskingPrice(t.SellPrice, t.Quality, options.PriceMultiplier));
        return (t.Name, t.Entry, price, stack ? (int)chosen.Item.Count : 1);
    }

    /// <summary>A reagent the bot's wanted recipes lack (a tradeable one), with how many, or null.</summary>
    private (string Name, uint Entry, uint Count)? Buying(Player player, SpellFeature? spells)
    {
        if (spells is null) return null;
        var supply = new PlayerbotVendorSupplyNeeds();
        supply.Refresh(player, spells.System.Store, spells.Spellbook);
        foreach ((uint itemId, uint missing) in supply.Shortfalls(player, 8))
            if (player.Inventory.Templates.Find(itemId) is { Name.Length: > 0 } template && PlayerbotVoiceFacts.Tradeable(template))
                return (template.Name, itemId, missing);
        return null;
    }

    // --- answering ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="PlayerbotChat.Responder"/>: a real player's whisper to a bot with an open call ("inv" to an LFG or LFM call, a question
    /// about a WTS call) is answered here (true: no other reply); a real player's "ding" in party or guild chat gets one bot's "gz".
    /// </summary>
    private bool Respond(BotHeardLine line)
    {
        if (_hooked is not { IsActive: true } chat || chat.VoiceOptions is not { Enabled: true } options || !chat.SpeechOptions.Enabled) return false;
        if (services.GetService<ManagedPlayerbotFeature>() is not { } bots || bots.FindPartyAI(line.BotId) is not { } party) return false;
        if (bots.FindSession(line.BotId)?.Player is not { } player || player.Guid != line.BotGuid) return false;
        if (line.SenderIsBot)
        {
            // Another bot's LFG or LFM line: the hearing bot may offer to join (the coordinator decides); no other line of a bot's is answered here.
            AnswerBotCall(chat, options, bots, line, player, party);
            return false;
        }

        if (line.Type == ChatType.Whisper && options.AnswerPosts)
            return Answer(chat, options, line, player, party);
        if (options.GroupCalls && line.FirstHearing && line.Type is ChatType.Party or ChatType.Raid or ChatType.RaidLeader or ChatType.Guild
            && PlayerbotVoiceFacts.IsDing(line.Text))
        {
            BotSpeechChannel channel = line.Type == ChatType.Guild ? BotSpeechChannel.Guild : BotSpeechChannel.Party;
            Say(chat, line.BotId, player, party, new BotSpeech(channel, string.Empty, "gz") { Target = line.SenderName }, "voice.gz",
                $"{line.SenderName} just levelled up.", $"Congratulate {line.SenderName} on the level up, very short.", null, ("who", line.SenderName));
        }

        return false;
    }

    /// <summary>
    /// A bot heard another bot's call for a group in a world channel (an LFG or LFM still on the board): it offers to the coordinator
    /// (<see cref="PlayerbotGroupCoordinator.OfferLfg"/>, with the <see cref="PlayerbotCooperationOptions.LfgAnswerBonus"/> chance). Only a bot
    /// the coordinator holds a place for ("pledged") answers in the channel, as a reply within the bot-to-bot turns: "inv me, ill tank".
    /// Any other hearer stays silent.
    /// </summary>
    private bool AnswerBotCall(PlayerbotChat chat, PlayerbotVoiceOptions options, ManagedPlayerbotFeature bots, BotHeardLine line, Player player, PlayerbotPartyAI party)
    {
        if (line.Type != ChatType.Channel || line.ChannelName is null || services.GetService<PlayerbotGroupCoordinator>() is not { } coordinator) return false;
        if (!bots.Social.TryBot(line.SenderGuid, out Guid posterId, out _)) return false;
        if (chat.Voice.Find(posterId, chat.NowMs, options.PostLifeSeconds * 1000L) is not { Kind: PlayerbotVoicePostKind.LookingForGroup } post) return false;
        PlayerbotLfgOffer offer = coordinator.OfferLfg(line.BotId, posterId, post.PostedMs);
        if (offer.Outcome != PlayerbotLfgOfferOutcome.Pledged) return false;
        int turn = chat.NextBotTurn(line.SenderGuid, line.BotGuid);
        if (turn <= 0) return false;
        BotSpeech reply = BotSpeech.InChannel(line.ChannelName, string.Empty, "recruit-reply", BotSpeechPriority.Normal)
            with { IsReply = true, ToBot = true, Turn = turn, Target = line.SenderName, TargetGuid = line.SenderGuid };
        return SayRecruit(chat, line.BotId, player, party, reply, offer.Role, post.What ?? string.Empty);
    }

    private bool SayRecruit(PlayerbotChat chat, Guid botId, Player player, PlayerbotPartyAI party, BotSpeech speech, PlayerbotGroupRole role, string what)
    {
        (string key, string sentence, string instruction) = role switch
        {
            PlayerbotGroupRole.Tank => ("voice.recruit.tank", "You offer to tank the group that is looking for more.", "Answer the call for a group: say you will tank. Very short."),
            PlayerbotGroupRole.Healer => ("voice.recruit.heal", "You offer to heal the group that is looking for more.", "Answer the call for a group: say you will heal. Very short."),
            _ => ("voice.recruit.dps", "You offer to join the group that is looking for more.", "Answer the call for a group: say you will come along. Very short."),
        };
        return Say(chat, botId, player, party, speech, key, sentence, instruction, null, ("task", what), ("dungeon", what));
    }

    /// <summary>
    /// The bots the coordinator recruited into a group (no call of theirs was answered) say they come: one line each in /say, "ill come, need
    /// a healer?" (<see cref="PlayerbotGroupCoordinator.TakeRecruitSays"/>). A bot that cannot speak now (cooldown, nobody to hear) stays silent.
    /// </summary>
    private void Recruits(PlayerbotSocialPass pass, PlayerbotChat chat)
    {
        if (services.GetService<PlayerbotGroupCoordinator>() is not { } coordinator) return;
        foreach ((Guid botId, PlayerbotGroupRole role) in coordinator.TakeRecruitSays())
        {
            if (_budget <= 0) return;
            PlayerbotSocialBot? bot = null;
            foreach (PlayerbotSocialBot running in pass.Bots)
                if (running.Id == botId) { bot = running; break; }
            if (bot is null || !bot.Player.IsAlive || !Spend()) continue;
            SayRecruit(chat, bot.Id, bot.Player, bot.Party, new BotSpeech(BotSpeechChannel.Say, string.Empty, "recruit", BotSpeechPriority.Normal), role, string.Empty);
        }
    }

    private bool Answer(PlayerbotChat chat, PlayerbotVoiceOptions options, BotHeardLine line, Player player, PlayerbotPartyAI party)
    {
        if (chat.Voice.Find(line.BotId, chat.NowMs, options.PostLifeSeconds * 1000L) is not { } post) return false;
        if (player.Session is not WorldSession session || session.World.FindOnlinePlayer(line.SenderGuid) is not { } asker) return false;
        BotSpeech reply = BotSpeech.WhisperTo(line.SenderName, line.SenderGuid, string.Empty, "voice-reply", BotSpeechPriority.High) with { IsReply = true };
        switch (post.Kind)
        {
            case PlayerbotVoicePostKind.LookingForGroup when PlayerbotVoiceFacts.AsksInvite(line.Text):
            {
                if (services.GetService<PlayerbotGroupCoordinator>() is not { } coordinator) return false;
                PlayerbotVolunteerResult result = coordinator.Volunteer(line.BotId, asker);
                (string key, string sentence) = result.Outcome switch
                {
                    PlayerbotVolunteerOutcome.Noted when result.Missing == 0 => ("voice.reply.lfg.ready", $"{asker.Name} wants to join your group for {post.What}; the group is complete with them."),
                    PlayerbotVolunteerOutcome.Noted => ("voice.reply.lfg.noted", $"{asker.Name} wants to join your group for {post.What}; you still need {Number(result.Missing)} more."),
                    PlayerbotVolunteerOutcome.Level => ("voice.reply.lfg.level", $"{asker.Name} is below level {Number(post.MinLevel)} for {post.What}."),
                    PlayerbotVolunteerOutcome.Grouped => ("voice.reply.lfg.grouped", $"{asker.Name} is already in a group."),
                    PlayerbotVolunteerOutcome.NotWaiting => ("voice.reply.lfg.gone", $"You are no longer looking for a group for {post.What}."),
                    _ => (string.Empty, string.Empty),
                };
                if (key.Length == 0) return false;
                if (result.Outcome == PlayerbotVolunteerOutcome.NotWaiting) chat.Voice.Forget(line.BotId);
                return Say(chat, line.BotId, player, party, reply, key, sentence, $"Answer {asker.Name} by whisper: {sentence}", null,
                    ("count", Number(result.Missing)), ("minlevel", Number(post.MinLevel)), ("dungeon", post.What ?? string.Empty));
            }

            case PlayerbotVoicePostKind.Selling when post.What is { } item && PlayerbotVoiceFacts.AsksAbout(line.Text, item):
            {
                if (player.Inventory.GetItemCount(post.ItemEntry) == 0)
                {
                    chat.Voice.Forget(line.BotId);
                    return Say(chat, line.BotId, player, party, reply, "voice.reply.wts.gone", $"You no longer have the {item} you offered.",
                        $"Tell {asker.Name} the {item} is gone.", null, ("item", item));
                }

                BotChatPersona persona = party.PersonaFor(player);
                string place = persona.Subzone ?? persona.Zone ?? string.Empty;
                return Say(chat, line.BotId, player, party, reply, "voice.reply.wts", $"{asker.Name} asks about the {item} you sell for {post.Price}.",
                    $"Tell {asker.Name} the price of {item} is {post.Price} and that you are in {place}.", persona,
                    ("item", item), ("price", post.Price ?? string.Empty), ("place", place));
            }

            default:
                return false;
        }
    }

    // --- housekeeping ----------------------------------------------------------------------------------------------------

    private void Prune(PlayerbotSocialPass pass)
    {
        if (pass.NowMs - _prunedMs < 60_000) return;
        _prunedMs = pass.NowMs;
        foreach ((Guid id, BotState state) in _bots.ToArray())
            if (pass.NowMs - state.SeenMs >= 600_000) _bots.Remove(id);
        foreach ((int id, RunState state) in _runs.ToArray())
            if (pass.NowMs - state.SeenMs >= 60_000) _runs.Remove(id);
        foreach (var (key, state) in _battlegrounds.ToArray())
            if (pass.NowMs - state.SeenMs >= 60_000) _battlegrounds.Remove(key);
        foreach ((string key, long at) in _calls.ToArray())
            if (pass.NowMs - at >= 600_000) _calls.Remove(key);
        foreach (KeyValuePair<((PlayerbotGroupGoalKind, uint) Goal, Team Team), long> hold in _holds.ToArray())
            if (pass.NowMs - hold.Value >= 3_600_000) _holds.Remove(hold.Key);
        if (_dings.Count > 512) _dings.Clear();
    }

    /// <summary>What the voice remembers of one bot.</summary>
    private sealed class BotState
    {
        public Player? Player;
        public bool Fresh = true;
        public long SeenMs;
        public uint Zone;
        public uint[]? Explored;
        public bool WasAlive;
        public bool RezAsked;
        public long FightSinceMs;
        public bool OomSaid;
        public bool HealsSaid;
        public bool IncSaid;
        public readonly HashSet<ulong> AggroSaid = [];
        public PlayerbotGoalKind? Goal;
        public HashSet<(ulong Caster, uint Spell)>? Buffs;
        public readonly Dictionary<ulong, long> Thanked = [];
        public int ShareAsked;
        public long LfgMs;
        public long TradeMs;
        public long QuestHelpMs;
        public bool HasLocality;
        public (PlayerbotGroupGoalKind, uint) LocalityKey;
        public long LocalityMs;
        public long LocalityTtlMs;
        public PlayerbotLfgLocalityVerdict Locality;

        public void ResetFight()
        {
            FightSinceMs = 0;
            OomSaid = HealsSaid = IncSaid = false;
            AggroSaid.Clear();
        }
    }

    /// <summary>What the voice saw of a run at the last pass.</summary>
    private sealed class RunState
    {
        public PlayerbotRunStep Step;
        public int Killed;
        public bool Resting;
        public string? Boss;
        public long SeenMs;
    }

    /// <summary>What the voice saw of a battleground at the last pass.</summary>
    private sealed class BattlegroundState
    {
        public readonly WsgFlagState[] Flags = [WsgFlagState.OnBase, WsgFlagState.OnBase];
        public readonly byte[] Nodes = new byte[ArathiBasin.NodeCount];
        public readonly Team?[] Owners = new Team?[ArathiBasin.NodeCount];
        public readonly int[] AvOwners = new int[AlteracValley.NodeCount];
        public readonly int[] AvStates = new int[AlteracValley.NodeCount];
        public int NextSpeaker;
        public long SeenMs;
    }
}

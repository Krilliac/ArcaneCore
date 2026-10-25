using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The one seam the coordinator, party AI, brain and chat record social events through. It knows which characters are managed bots
/// (a player guid carries the character id, so the map is keyed by it), keeps the <see cref="PlayerbotSocialMemory"/>, and never throws
/// into game logic: a failure is recorded at the fault site <c>playerbot.social-memory</c> and logged. Any thread.
/// </summary>
internal sealed class PlayerbotSocialRecorder(PlayerbotSocialMemory memory, ILogger logger, PlayerbotTradeLedger? ledger = null)
{
    public const string FaultSite = "playerbot.social-memory";

    private readonly ConcurrentDictionary<uint, (Guid BotId, int CharacterId)> _managed = new();
    private int _faultsLogged;

    public PlayerbotSocialMemory Memory { get; } = memory;

    /// <summary>The bots' trade ledgers (P4 S3); events that belong in them are fed from <see cref="Note"/> and <see cref="NoteById"/>.</summary>
    public PlayerbotTradeLedger? Ledger { get; } = ledger;

    /// <summary>The bots' short-term moods; every social event that moves one is applied by <see cref="Note"/> and <see cref="NoteById"/>.</summary>
    public PlayerbotMood Mood { get; } = new();

    public bool Enabled => Memory.Enabled;

    /// <summary>
    /// Whether a character is in a group-coordinator siege group (wired by the managed feature; null: none is). A kill with either side in one is organised
    /// PvP, not a grudge, unless <see cref="PlayerbotSocialMemoryOptions.OrganisedPvpKills"/> is on. Read on every PvP kill, any thread.
    /// </summary>
    public Func<ObjectGuid, bool>? InSiegeGroup { get; set; }

    /// <summary>The server-wide "rivals off" store the social feature owns (wired by the managed feature; null: bots take no such whisper).</summary>
    public PlayerbotRivalryOptOut? RivalryOptOut { get; set; }

    /// <summary>A managed bot logged in (or was adopted by this feature): its events are now recorded against its id.</summary>
    public void Track(Guid botId, int characterId)
    {
        if (characterId <= 0) return;
        _managed[(uint)characterId] = (botId, characterId);
        Memory.SetSelf(botId, ObjectGuid.Player((uint)characterId).Value);
    }

    /// <summary>A managed bot left this feature.</summary>
    public void Untrack(int characterId) => _managed.TryRemove((uint)characterId, out _);

    /// <summary>The managed bot a player guid belongs to.</summary>
    public bool TryBot(ObjectGuid guid, out Guid botId, out int characterId)
    {
        if (guid.IsPlayer && _managed.TryGetValue(guid.Low, out (Guid BotId, int CharacterId) bot))
        {
            botId = bot.BotId;
            characterId = bot.CharacterId;
            return true;
        }

        botId = Guid.Empty;
        characterId = 0;
        return false;
    }

    public bool IsManaged(ObjectGuid guid) => guid.IsPlayer && _managed.ContainsKey(guid.Low);

    /// <summary>Record <paramref name="e"/> in <paramref name="bot"/>'s memory toward <paramref name="other"/>; nothing if the first is not a managed bot or the second not a player.</summary>
    public void Note(ObjectGuid bot, ObjectGuid other, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!other.IsPlayer || !TryBot(bot, out Guid botId, out int characterId)) return;
        if (IsLedgerEvent(e)) Guarded((Recorder: this, botId, characterId, Other: other.Value, e), static s => s.Recorder.FeedLedger(s.botId, s.characterId, s.Other, s.e)); // the ledger is fed whether or not the memory is on: scam safety does not depend on that switch
        if (!Enabled) return;
        Guarded(() =>
        {
            Memory.Note(botId, characterId, other.Value, IsManaged(other), e, otherFaction);
            Mood.Nudge(botId, PlayerbotMood.DeltaOf(e));
        });
    }

    // The events that belong in the trade ledger as well as in the memory. A gift's once-a-day rule is the ledger's: the caller that counts gifts asks it (NoteGiftReceived)
    // before recording, and this stamp then finds the day already taken. A flake stays a memory event only.
    private static bool IsLedgerEvent(PlayerbotSocialEvent e)
        => e is PlayerbotSocialEvent.FairDeal or PlayerbotSocialEvent.TradeScam or PlayerbotSocialEvent.NinjaLoot or PlayerbotSocialEvent.GiftReceived;

    private void FeedLedger(Guid botId, int characterId, ulong other, PlayerbotSocialEvent e)
    {
        if (Ledger is not { } ledger || !_managed.ContainsKey((uint)characterId)) return; // a bot that left (deleted) must not bring its ledger back
        switch (e)
        {
            case PlayerbotSocialEvent.FairDeal: ledger.NoteFairDeal(botId, characterId, other); break;
            case PlayerbotSocialEvent.TradeScam: ledger.NoteScam(botId, characterId, other); break;
            case PlayerbotSocialEvent.NinjaLoot: ledger.NoteNinja(botId, characterId, other); break;
            case PlayerbotSocialEvent.GiftReceived: ledger.NoteGiftReceived(botId, characterId, other); break;
        }
    }

    /// <summary>Record <paramref name="e"/> for a bot whose id is already known (chat, the party AI).</summary>
    public void NoteById(Guid botId, ObjectGuid botGuid, ObjectGuid other, PlayerbotSocialEvent e, bool otherFaction = false)
    {
        if (!other.IsPlayer || other == botGuid) return;
        if (IsLedgerEvent(e)) Guarded((Recorder: this, botId, CharacterId: (int)botGuid.Low, Other: other.Value, e), static s => s.Recorder.FeedLedger(s.botId, s.CharacterId, s.Other, s.e)); // the ledger is fed whether or not the memory is on
        if (!Enabled) return;
        Guarded(() =>
        {
            Memory.Note(botId, (int)botGuid.Low, other.Value, IsManaged(other), e, otherFaction);
            Mood.Nudge(botId, PlayerbotMood.DeltaOf(e));
        });
    }

    /// <summary>Record <paramref name="e"/> both ways between two characters (each side that is a managed bot remembers the other).</summary>
    public void NoteBoth(ObjectGuid a, ObjectGuid b, PlayerbotSocialEvent e, bool crossFaction = false)
    {
        if (a == b) return;
        Note(a, b, e, crossFaction);
        Note(b, a, e, crossFaction);
    }

    /// <summary>How long the credit between two characters lasts (one fight; either of them leaving combat ends it sooner, <see cref="EndFight"/>).</summary>
    internal const long HelpWindowMs = 60_000;

    // Keyed by the unordered pair: two bots fighting the same mob each see the other on its threat list, and that is one help, not two.
    private readonly Dictionary<(ulong Low, ulong High), long> _helped = [];
    private readonly object _helpGate = new();
    private Func<long> _now = static () => Environment.TickCount64;

    /// <summary>The clock of the help window (tests).</summary>
    internal Func<long> Clock { set => _now = value; }

    /// <summary>
    /// <paramref name="helper"/> helped <paramref name="victim"/> in the current fight (an assist on a tagged mob, a heal, a hit on the mob that fights
    /// the victim): the victim, if a managed bot, records HelpedBy and the helper, if a managed bot, records HelpedThem. Once per pair of
    /// characters per fight, whichever source reports it first and in whichever direction, so a single assist is never counted twice (an assist
    /// the helper bot reports and the same bot's own fight read of the tagger are one help). <paramref name="kind"/> says how it helped, for the thanks.
    /// </summary>
    public void NoteHelp(Unit helper, Unit victim, PlayerbotThanksKind kind = PlayerbotThanksKind.Help)
    {
        if (!Enabled || helper.Guid == victim.Guid || !helper.Guid.IsPlayer || !victim.Guid.IsPlayer) return;
        if (!IsManaged(helper.Guid) && !IsManaged(victim.Guid)) return;
        Guarded(() =>
        {
            long now = _now();
            lock (_helpGate)
            {
                if (_helped.Count > 2048)
                    foreach (KeyValuePair<(ulong Low, ulong High), long> pair in _helped) // Dictionary.Remove during enumeration is allowed: no copy
                        if (now - pair.Value > HelpWindowMs) _helped.Remove(pair.Key);
                (ulong, ulong) key = helper.Guid.Value < victim.Guid.Value ? (helper.Guid.Value, victim.Guid.Value) : (victim.Guid.Value, helper.Guid.Value);
                if (_helped.TryGetValue(key, out long at) && now - at <= HelpWindowMs) return;
                _helped[key] = now;
            }

            bool crossFaction = helper is Player a && victim is Player b && a.Team != b.Team;
            Note(helper.Guid, victim.Guid, PlayerbotSocialEvent.HelpedThem, crossFaction);
            Note(victim.Guid, helper.Guid, PlayerbotSocialEvent.HelpedBy, crossFaction);
            // A managed bot that was helped owes thanks (PlayerbotGratitudeLane); the pair window above makes it one per helper per fight.
            if (IsManaged(victim.Guid) && _thanks.Count < 256) _thanks.Enqueue((victim.Guid, helper.Guid, kind));
        });
    }

    private readonly ConcurrentQueue<(ObjectGuid Bot, ObjectGuid Helper, PlayerbotThanksKind Kind)> _thanks = new();

    /// <summary>Thanks waiting for the social pass (tests).</summary>
    internal int PendingThanks => _thanks.Count;

    /// <summary>Take the oldest thanks a bot owes: who was helped, who helped and how.</summary>
    internal bool TryTakeThanks(out ObjectGuid bot, out ObjectGuid helper, out PlayerbotThanksKind kind)
    {
        if (_thanks.TryDequeue(out (ObjectGuid Bot, ObjectGuid Helper, PlayerbotThanksKind Kind) owed))
        {
            (bot, helper, kind) = owed;
            return true;
        }

        bot = helper = default;
        kind = PlayerbotThanksKind.Help;
        return false;
    }

    /// <summary>
    /// A player died to another player (or to a player's pet or totem): the victim, if a managed bot, records KilledBy and the killer, if a managed
    /// bot, KilledThem, each marking the other as of the other faction when the teams differ. Duels never count: a duel ends at 1 hp without a kill,
    /// and a death inside a started duel is skipped all the same. No record for an environmental death or a suicide.
    /// </summary>
    public void NotePvpKill(Unit? killer, Unit victim)
        => Guarded((Recorder: this, Killer: killer, Victim: victim), static s => s.Recorder.NotePvpKillCore(s.Killer, s.Victim)); // no closure: every player death on every map passes here

    private void NotePvpKillCore(Unit? killer, Unit victim)
    {
        if (!Enabled || killer is null || victim is not Player dead) return;
        Player? slayer = DuelRules.ControllingPlayer(killer);
        if (slayer is null || ReferenceEquals(slayer, dead) || DuelRules.IsInDuelWith(slayer, dead) || DuelRules.IsInDuelWith(dead, slayer)) return;
        if (!IsManaged(slayer.Guid) && !IsManaged(dead.Guid)) return;
        if (!Memory.OrganisedPvpKills && IsOrganised(slayer, dead)) return;
        bool crossFaction = slayer.Team != dead.Team;
        Note(dead.Guid, slayer.Guid, PlayerbotSocialEvent.KilledBy, crossFaction);
        Note(slayer.Guid, dead.Guid, PlayerbotSocialEvent.KilledThem, crossFaction);
    }

    // A battleground map, or either side in a coordinator siege group: organised PvP (a match, a GM-ordered siege), not a personal grudge.
    private bool IsOrganised(Player slayer, Player dead)
        => (dead.Map ?? slayer.Map)?.Template is { IsBattleground: true } || InSiegeGroup is { } inSiege && (inSiege(slayer.Guid) || inSiege(dead.Guid));

    /// <summary>A character left combat: the help credits it is part of end with its fight, so the next fight can earn them again.</summary>
    public void EndFight(ObjectGuid character)
    {
        lock (_helpGate)
            if (_helped.Count > 0)
                foreach ((ulong Low, ulong High) key in _helped.Keys)
                    if (key.Low == character.Value || key.High == character.Value) _helped.Remove(key);
    }

    /// <summary>
    /// A line addressed to a bot: an exchange with its sender and, if the line apologises, an apology. The apology is recorded first, so the
    /// exchange's own affinity cannot lift a small grudge to zero before the apology looks for it.
    /// </summary>
    public void NoteChatLine(Guid botId, ObjectGuid botGuid, ObjectGuid sender, string? text)
    {
        if (sender.IsPlayer && (TakeExpected(botId, sender) || _replies.Count < 256)) EnqueueReply(botId, sender, text);
        if (PlayerbotApology.IsApology(text)) NoteById(botId, botGuid, sender, PlayerbotSocialEvent.Apologized);
        NoteById(botId, botGuid, sender, PlayerbotSocialEvent.ChatExchange);
    }

    /// <summary>Record the role <paramref name="other"/> played in a group with <paramref name="bot"/> (replacing the earlier one); nothing if the first is not a managed bot or no relation exists yet.</summary>
    public void SetRole(ObjectGuid bot, ObjectGuid other, PlayerbotRelationFlags role)
    {
        if (!Enabled || !other.IsPlayer || !TryBot(bot, out Guid botId, out _)) return;
        Guarded(() => Memory.SetRole(botId, other.Value, role));
    }

    /// <summary>Set the last topic and the bot-side summary of an existing relation.</summary>
    public void SetTopic(Guid botId, ObjectGuid other, string topicKey, string summary)
    {
        if (!Enabled || !other.IsPlayer) return;
        Guarded(() => Memory.SetTopic(botId, other.Value, topicKey, summary));
    }

    private readonly ConcurrentQueue<(Guid BotId, ObjectGuid Sender, string? Text)> _replies = new();

    private int _repliesHeard;

    /// <summary>Replies enqueued for the initiative lane so far (tests wait on it instead of a delay).</summary>
    internal int RepliesHeard => Volatile.Read(ref _repliesHeard);

    private void EnqueueReply(Guid botId, ObjectGuid sender, string? text)
    {
        _replies.Enqueue((botId, sender, text));
        Interlocked.Increment(ref _repliesHeard);
    }

    private readonly ConcurrentDictionary<(Guid Bot, ulong Player), byte> _expected = new();

    /// <summary>The initiative lane opened with <paramref name="player"/> and waits for the answer: their next say within the bot's hearing is it, named or not.</summary>
    internal bool Expect(Guid botId, ObjectGuid player)
    {
        if (_expected.Count >= MaxExpected && !_expected.ContainsKey((botId, player.Value))) return false;
        _expected[(botId, player.Value)] = 1;
        return true;
    }

    /// <summary>How many openings may be awaited at once.</summary>
    internal const int MaxExpected = 1024;

    /// <summary>Whether <paramref name="player"/> is awaited by the bot and has not answered yet; the answer is then taken (a pending opening's reply is never dropped for a full queue).</summary>
    private bool TakeExpected(Guid botId, ObjectGuid player) => _expected.TryUpdate((botId, player.Value), 2, 1);

    /// <summary>The answer came (or the wait is over): the bot no longer listens for it.</summary>
    internal void Unexpect(Guid botId, ObjectGuid player) => _expected.TryRemove((botId, player.Value), out _);

    /// <summary>Forget every awaited answer (the lane is switched off).</summary>
    internal void ClearExpected() => _expected.Clear();

    /// <summary>
    /// A say the bot heard that is not addressed to it: when the sender is the player it is waiting on, it is the answer to its opening. Called from the chat
    /// service's line hook (world thread); anything else is ignored.
    /// </summary>
    public void NoteHeardSay(Guid botId, ObjectGuid sender, string? text)
    {
        if (!sender.IsPlayer) return;
        // The first line of an awaited player is always kept; a later one (before the lane has read the first) follows the shared cap.
        if (TakeExpected(botId, sender) || (_expected.ContainsKey((botId, sender.Value)) && _replies.Count < 256)) EnqueueReply(botId, sender, text);
    }

    /// <summary>Take the oldest line a character addressed to a bot (the answer to an opening, <see cref="PlayerbotInitiativeLane"/>).</summary>
    internal bool TryTakeReply(out Guid botId, out ObjectGuid sender, out string? text)
    {
        if (_replies.TryDequeue(out (Guid BotId, ObjectGuid Sender, string? Text) reply))
        {
            (botId, sender, text) = reply;
            return true;
        }

        botId = Guid.Empty;
        sender = default;
        text = null;
        return false;
    }

    private readonly ConcurrentDictionary<Guid, long> _tradesDone = new();

    private const long TradeDoneLifeMs = 5 * 60_000;

    private const int MaxTradesDone = 256;

    /// <summary>A trade the bot was in completed (its own trade status 8): the gratitude lane counts a tip as paid from it. A note stays 5 minutes and at most 256 are kept.</summary>
    public void NoteTradeDone(Guid botId)
    {
        long now = Environment.TickCount64;
        if (_tradesDone.Count >= MaxTradesDone)
        {
            foreach (KeyValuePair<Guid, long> entry in _tradesDone)
                if (now - entry.Value > TradeDoneLifeMs) _tradesDone.TryRemove(entry.Key, out _);
            if (_tradesDone.Count >= MaxTradesDone && !_tradesDone.ContainsKey(botId))
            {
                // Still full of live notes: the oldest makes room.
                Guid oldest = _tradesDone.MinBy(entry => entry.Value).Key;
                _tradesDone.TryRemove(oldest, out _);
            }
        }

        _tradesDone[botId] = now;
    }

    /// <summary>Take the note that the bot's trade completed; false when none has since the last take, or when it is older than 5 minutes.</summary>
    internal bool TryTakeTradeDone(Guid botId) => _tradesDone.TryRemove(botId, out long at) && Environment.TickCount64 - at <= TradeDoneLifeMs;

    private readonly ConcurrentQueue<(ObjectGuid Bot, ObjectGuid Asker)> _helpCalls = new();

    /// <summary>
    /// <paramref name="asker"/> called for help within earshot of the managed bot with the character <paramref name="bot"/> (a help emote aimed at it, or a help yell
    /// or say it heard). The social pass answers the queue (<see cref="PlayerbotHelpLane"/>); at most 256 calls wait.
    /// </summary>
    public void NoteHelpCall(ObjectGuid bot, ObjectGuid asker)
    {
        if (_helpCalls.Count < 256) _helpCalls.Enqueue((bot, asker));
    }

    /// <summary>Calls for help waiting for the social pass (tests).</summary>
    internal int PendingHelpCalls => _helpCalls.Count;

    /// <summary>Take the oldest call for help that has not been answered.</summary>
    internal bool TryTakeHelpCall(out ObjectGuid bot, out ObjectGuid asker)
    {
        if (_helpCalls.TryDequeue(out (ObjectGuid Bot, ObjectGuid Asker) call))
        {
            (bot, asker) = call;
            return true;
        }

        bot = default;
        asker = default;
        return false;
    }

    /// <summary>Run a recording step so that it can never throw into game logic.</summary>
    public void Guarded(Action step)
    {
        try
        {
            step();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Fault(error);
        }
    }

    /// <summary>As <see cref="Guarded(Action)"/>, for a step that carries its state in <paramref name="state"/>: with a static lambda nothing is allocated (the per-think paths).</summary>
    public void Guarded<TState>(TState state, Action<TState> step)
    {
        try
        {
            step(state);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Fault(error);
        }
    }

    private void Fault(Exception error)
    {
        FaultLedger.Shared.Record(FaultSite, error);
        if (Interlocked.Increment(ref _faultsLogged) <= 20)
            logger.LogWarning(error, "Playerbot social memory: a recording step failed");
    }
}

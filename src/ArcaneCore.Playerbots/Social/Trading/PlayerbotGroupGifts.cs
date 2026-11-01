using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Where a bot's group gift stands (<see cref="PlayerbotGroupGifts"/>).</summary>
internal enum PlayerbotGiftPhase : byte
{
    /// <summary>No offer.</summary>
    Idle,

    /// <summary>The "anyone need" line was said; the bot listens for a claim for <c>Social:Trading:Gifts:GiftClaimSeconds</c>.</summary>
    Offered,

    /// <summary>A groupmate is to get the item: the bot opens the trade, puts the item in and accepts once the partner did.</summary>
    Claimed,
}

/// <summary>An item the bot may offer: its guid, bag position and the vendor value of the whole stack in copper.</summary>
internal readonly record struct PlayerbotGiftCandidate(ObjectGuid Item, uint Entry, byte Bag, byte Slot, long Value);

/// <summary>
/// The group gift flow of one bot (<c>World:Playerbots:Social:Trading:Gifts</c>, docs/design/playerbot-social.md, "Group gifts"): after a fight a bot that holds a piece
/// of equipment it has no use for, worth no more than <c>MaxGiftValueCopper</c> (that cap is stated at level 60 and scales down with the bot's level), says "anyone need
/// {item}?" in group chat and listens for <c>GiftClaimSeconds</c>. The first groupmate who claims it ("me", "need", "mine"...) gets it through the trade window; with no
/// claim, a groupmate bot for whom it is an upgrade is handed it when the window closes. Only an offer's item is ever put into the trade window. This type is the state
/// machine and the pure rules; the party AI does the world work (inventory scan, chat line, packets) and asks this type what is due. Thread affinity: the bot's world thread
/// (one instance per party AI; no lock). The clock is the party AI's, in milliseconds.
/// </summary>
internal sealed class PlayerbotGroupGifts(PlayerbotOptions options)
{
    /// <summary>How long after a fight ends the bot waits before it offers anything (the loot and the first rolls are over).</summary>
    internal const long ArmAfterCombatMs = 4_000;

    /// <summary>The least gap between two offers of one bot.</summary>
    internal const long CooldownMs = 120_000;

    /// <summary>How long a claimed gift may take (the claimer walks up, the window opens, both accept) before the bot gives up.</summary>
    internal const long TradeMaxMs = 30_000;

    /// <summary>Items the bot remembers having offered (so a refused item is not offered again at once).</summary>
    internal const int MaxRemembered = 32;

    private readonly HashSet<ObjectGuid> _offered = [];
    private bool _inCombat;
    private long _combatEndedMs = long.MinValue;
    private long _cooldownUntilMs = long.MinValue;
    private long _phaseSinceMs;

    private PlayerbotSocialTradingGiftsOptions Gifts => options.Social.Trading.Gifts;

    /// <summary>The phase (see <see cref="PlayerbotGiftPhase"/>).</summary>
    internal PlayerbotGiftPhase Phase { get; private set; }

    /// <summary>The item on offer (empty while idle).</summary>
    internal ObjectGuid Item { get; private set; }

    /// <summary>The groupmate who claimed it (empty until claimed).</summary>
    internal ObjectGuid Recipient { get; private set; }

    /// <summary>Whether group gifts run: <c>Social:Trading:Enabled</c>, <c>Gifts:Enabled</c> and a <c>MaxGiftValueCopper</c> above 0. Read live.</summary>
    internal bool Enabled => options.Social.Trading.Enabled && Gifts.Enabled && Gifts.MaxGiftValueCopper > 0;

    /// <summary>The most a gift of a bot of <paramref name="level"/> may be worth: <c>MaxGiftValueCopper</c> at level 60, scaled down by the level below it (at least level 1).</summary>
    internal long MaxValue(int level) => (long)Gifts.MaxGiftValueCopper * Math.Clamp(level, 1, 60) / 60;

    /// <summary>Whether a gift worth <paramref name="valueCopper"/> is within the cap for a bot of <paramref name="level"/> (a worthless item is not offered either).</summary>
    internal bool ValueAllowed(long valueCopper, int level) => valueCopper > 0 && valueCopper <= MaxValue(level);

    /// <summary>The claim window in milliseconds (<c>GiftClaimSeconds</c>).</summary>
    internal long ClaimWindowMs => Gifts.GiftClaimSeconds * 1000L;

    /// <summary>Whether a group line claims the offered item: a short answer that says "me", "mine", "need" or "I'll take it".</summary>
    internal static bool IsClaim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        string line = text.Trim().Trim('!', '.', '?', ' ').ToLowerInvariant();
        return line is "me" or "mine" or "need" or "i need" or "i need it" or "i need that" or "i want it" or "me please" or "me pls" or "me plz" or "i'll take it" or "ill take it"
            or "need it" or "need that" or "yes" or "yes please" or "me me" or "i'll take that" or "ill take that";
    }

    /// <summary>
    /// Feed the combat state of this tick. True when an offer is due now: the flow is idle, no cooldown runs and the bot has been out of combat for
    /// <see cref="ArmAfterCombatMs"/> after a fight (a bot that never fought does not offer).
    /// </summary>
    internal bool OfferDue(bool inCombat, long nowMs)
    {
        if (inCombat)
        {
            _inCombat = true;
            return false;
        }

        if (_inCombat)
        {
            _inCombat = false;
            _combatEndedMs = nowMs;
        }

        return Phase == PlayerbotGiftPhase.Idle && _combatEndedMs != long.MinValue && nowMs - _combatEndedMs >= ArmAfterCombatMs && nowMs >= _cooldownUntilMs;
    }

    /// <summary>Whether <paramref name="item"/> was offered before (the party AI skips it).</summary>
    internal bool WasOffered(ObjectGuid item) => _offered.Contains(item);

    /// <summary>The most valuable of <paramref name="candidates"/> within the cap that was not offered before and passes <paramref name="wanted"/> (null when none).</summary>
    internal PlayerbotGiftCandidate? Choose(IEnumerable<PlayerbotGiftCandidate> candidates, int level, Func<PlayerbotGiftCandidate, bool> wanted)
    {
        PlayerbotGiftCandidate? best = null;
        foreach (PlayerbotGiftCandidate candidate in candidates)
        {
            if (!ValueAllowed(candidate.Value, level) || WasOffered(candidate.Item) || (best is { } b && candidate.Value <= b.Value) || !wanted(candidate)) continue;
            best = candidate;
        }

        return best;
    }

    /// <summary>The "anyone need" line was said for <paramref name="item"/>: the claim window starts.</summary>
    internal void Begin(ObjectGuid item, long nowMs)
    {
        if (_offered.Count >= MaxRemembered) _offered.Clear();
        _offered.Add(item);
        Item = item;
        Recipient = ObjectGuid.Empty;
        Phase = PlayerbotGiftPhase.Offered;
        _phaseSinceMs = nowMs;
        _combatEndedMs = long.MinValue; // the next offer needs its own fight
        _inCombat = false;
    }

    /// <summary>Whether the claim window ended without a claim.</summary>
    internal bool ClaimWindowOver(long nowMs) => Phase == PlayerbotGiftPhase.Offered && nowMs - _phaseSinceMs >= ClaimWindowMs;

    /// <summary>A groupmate claims the item: true when it was on offer inside the window (the first claim wins); the flow moves to <see cref="PlayerbotGiftPhase.Claimed"/>.</summary>
    internal bool Claim(ObjectGuid claimer, long nowMs)
    {
        if (Phase != PlayerbotGiftPhase.Offered || claimer.IsEmpty || ClaimWindowOver(nowMs)) return false;
        return Award(claimer, nowMs);
    }

    /// <summary>The window ended unclaimed and the party AI found a groupmate bot the item is an upgrade for: it gets the item.</summary>
    internal bool Award(ObjectGuid recipient, long nowMs)
    {
        if (Phase != PlayerbotGiftPhase.Offered || recipient.IsEmpty) return false;
        Recipient = recipient;
        Phase = PlayerbotGiftPhase.Claimed;
        _phaseSinceMs = nowMs;
        return true;
    }

    /// <summary>Whether a claimed gift ran out of time (<see cref="TradeMaxMs"/>).</summary>
    internal bool TradeTimedOut(long nowMs) => Phase == PlayerbotGiftPhase.Claimed && nowMs - _phaseSinceMs >= TradeMaxMs;

    /// <summary>The flow ended (the trade completed or failed, nobody claimed, the group went): back to idle with the cooldown running.</summary>
    internal void End(long nowMs)
    {
        Phase = PlayerbotGiftPhase.Idle;
        Item = ObjectGuid.Empty;
        Recipient = ObjectGuid.Empty;
        _cooldownUntilMs = nowMs + CooldownMs;
        _combatEndedMs = long.MinValue; // the next offer needs its own fight
        _inCombat = false;
    }

    /// <summary>The group is gone: back to idle with no cooldown and no memory of the fight.</summary>
    internal void Reset()
    {
        Phase = PlayerbotGiftPhase.Idle;
        Item = ObjectGuid.Empty;
        Recipient = ObjectGuid.Empty;
        _combatEndedMs = long.MinValue;
        _inCombat = false;
    }
}

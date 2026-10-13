using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots.Groups;
using System.Runtime.CompilerServices;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Contention</c>: how bots share the mobs of one place instead of piling onto one (docs/areas/playbots.md, Contention).
/// Every key is live: <c>.reload config</c> changes this object in place and a bot reads it at its next think. Not vmangos keys.
/// </summary>
public sealed class PlayerbotContentionOptions
{
    /// <summary>
    /// Base chance (0..1) that a bot walking to a mob another player has tagged decides to help that player instead of leaving the mob
    /// alone. It is scaled by the bot's own sociability (a stable 0..1 from its guid), doubled for a real player's tag and raised by half
    /// when both share the kill quest, and only asked when the tagger is within 30 yards and losing and the bot is healthy. 0 never helps.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 1 ('.' is the decimal point); 0 never helps, 1 helps whenever the other conditions hold")]
    public float HelpChance { get; set; } = 0.35f;

    /// <summary>
    /// Yards a mob counts as farther away for each other bot already walking to it (0..200), when a bot picks among untagged mobs.
    /// 0 turns the soft claims off, so every bot goes for the nearest mob again.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a number from 0 to 200 ('.' is the decimal point); 0 turns the soft claims off")]
    public float ClaimPenaltyYards { get; set; } = 25f;

    /// <summary>Seconds a bot leaves a mob alone after it found it to be someone else's (1..600), and the least between two decisions about the same mob.</summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 600 (seconds)")]
    public int SomeoneElsesMemorySeconds { get; set; } = 15;

    /// <summary>
    /// Minutes a bot leaves a quest creature alone after three minutes of contested hunting without credit (1..120; see <see cref="PlayerbotContention.ObserveObjective"/>): it hunts its next
    /// unfinished objective or quest meanwhile, and asks for a group when none is left.
    /// </summary>
    [ArcaneCore.Kernel.Configuration.ConfigAllowed("a whole number from 1 to 120 (minutes)")]
    public int ContentionSuspendMinutes { get; set; } = 5;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Contention";
        if (CheckChance(HelpChance) is { } chance) throw new InvalidOperationException($"{section}: HelpChance {chance}.");
        if (CheckPenalty(ClaimPenaltyYards) is { } penalty) throw new InvalidOperationException($"{section}: ClaimPenaltyYards {penalty}.");
        if (CheckMemory(SomeoneElsesMemorySeconds) is { } memory) throw new InvalidOperationException($"{section}: SomeoneElsesMemorySeconds {memory}.");
        if (CheckSuspend(ContentionSuspendMinutes) is { } suspend) throw new InvalidOperationException($"{section}: ContentionSuspendMinutes {suspend}.");
    }

    internal static string? CheckChance(float value) => float.IsFinite(value) && value is >= 0f and <= 1f ? null : "must be 0..1";

    internal static string? CheckPenalty(float value) => float.IsFinite(value) && value is >= 0f and <= 200f ? null : "must be 0..200";

    internal static string? CheckMemory(int value) => value is >= 1 and <= 600 ? null : "must be 1..600";

    internal static string? CheckSuspend(int value) => value is >= 1 and <= 120 ? null : "must be 1..120";
}

/// <summary>
/// The soft claims of the bots on untagged mobs: mob guid to (first claimant, claimant count, expiry). A bot claims the mob it walks to
/// and refreshes the claim at each think; the claim ends when the bot tags the mob, it dies, the bot drops it or it expires
/// (<see cref="ClaimLifeMs"/>). Another bot weighs a claimed mob farther away (<see cref="PlayerbotContentionOptions.ClaimPenaltyYards"/>
/// per other claimant), so a crowd of bots spreads over the mobs near it.
/// <para>
/// Thread affinity: world thread only, no lock. Cost: every operation is one dictionary probe on a table sized once
/// (<see cref="WarmCapacity"/>); nothing is allocated while the table stays within it. Expired claims are dropped lazily: a lookup treats
/// an expired claim as absent, and the table is swept when it fills.
/// </para>
/// </summary>
internal sealed class PlayerbotClaims
{
    /// <summary>A claim lapses this long after the last refresh.</summary>
    internal const uint ClaimLifeMs = 8_000;

    /// <summary>The table is sized for this many claims once (about a thousand bots, each claiming one mob, with room to spare).</summary>
    internal const int WarmCapacity = 4096;

    /// <summary>No more claims than this are kept; a bot past it goes unclaimed (its pick is then the plain nearest).</summary>
    internal const int HardCapacity = 16384;

    private struct Claim
    {
        public ulong Owner;
        public int Count;
        public uint ExpiresMs;
        public uint Epoch;
    }

    private readonly Dictionary<ulong, Claim> _claims;
    private uint _epoch;

    internal PlayerbotClaims(int capacity = WarmCapacity) => _claims = new Dictionary<ulong, Claim>(capacity);

    /// <summary>Claims held in the table, expired ones not yet dropped included (tests).</summary>
    internal int Count => _claims.Count;

    /// <summary>How many bots other than the asker claim <paramref name="mob"/> now; <paramref name="mine"/> says the asker holds a claim on it.</summary>
    internal int OthersOf(ulong mob, bool mine, uint nowMs)
    {
        if (!_claims.TryGetValue(mob, out Claim claim) || unchecked((int)(claim.ExpiresMs - nowMs)) <= 0) return 0;
        return Math.Max(0, claim.Count - (mine ? 1 : 0));
    }

    /// <summary>The bot that first claimed <paramref name="mob"/> and still holds it (0 when none; tests and inspection).</summary>
    internal ulong OwnerOf(ulong mob, uint nowMs)
        => _claims.TryGetValue(mob, out Claim claim) && unchecked((int)(claim.ExpiresMs - nowMs)) > 0 ? claim.Owner : 0;

    /// <summary>The claimants of <paramref name="mob"/> now (0 when none; tests and inspection).</summary>
    internal int ClaimantsOf(ulong mob, uint nowMs) => OthersOf(mob, false, nowMs);

    /// <summary>Join the claimants of <paramref name="mob"/>. The epoch to give back to <see cref="Refresh"/> and <see cref="Leave"/>; 0 when the table is full.</summary>
    internal uint Join(ulong mob, ulong bot, uint nowMs)
    {
        if (_claims.TryGetValue(mob, out Claim claim) && unchecked((int)(claim.ExpiresMs - nowMs)) > 0)
        {
            claim.Count++;
            claim.ExpiresMs = unchecked(nowMs + ClaimLifeMs);
            _claims[mob] = claim;
            return claim.Epoch;
        }

        if (_claims.Count >= WarmCapacity && !_claims.ContainsKey(mob))
        {
            Sweep(nowMs);
            if (_claims.Count >= HardCapacity) return 0;
        }

        if (++_epoch == 0) ++_epoch;
        _claims[mob] = new Claim { Owner = bot, Count = 1, ExpiresMs = unchecked(nowMs + ClaimLifeMs), Epoch = _epoch };
        return _epoch;
    }

    /// <summary>Extend the claim of the epoch <see cref="Join"/> gave; false when it lapsed or was replaced (join again).</summary>
    internal bool Refresh(ulong mob, uint epoch, uint nowMs)
    {
        if (!_claims.TryGetValue(mob, out Claim claim) || claim.Epoch != epoch || unchecked((int)(claim.ExpiresMs - nowMs)) <= 0) return false;
        claim.ExpiresMs = unchecked(nowMs + ClaimLifeMs);
        _claims[mob] = claim;
        return true;
    }

    /// <summary>Leave the claim of the epoch <see cref="Join"/> gave; the last claimant out removes it. A claim that lapsed and was replaced is not touched.</summary>
    internal void Leave(ulong mob, uint epoch)
    {
        if (!_claims.TryGetValue(mob, out Claim claim) || claim.Epoch != epoch) return;
        if (--claim.Count <= 0) _claims.Remove(mob);
        else _claims[mob] = claim;
    }

    /// <summary>Drop the expired claims (removing during enumeration is allowed by Dictionary and allocates nothing).</summary>
    private void Sweep(uint nowMs)
    {
        foreach (KeyValuePair<ulong, Claim> pair in _claims)
            if (unchecked((int)(pair.Value.ExpiresMs - nowMs)) <= 0)
                _claims.Remove(pair.Key);
    }
}

/// <summary>Why a bot walking to a mob another player tagged helps that player or leaves the mob alone (<see cref="PlayerbotContention.Decide"/>).</summary>
internal enum PlayerbotHelpDecision : byte
{
    /// <summary>The bot helps: it attacks the mob (or heals the tagger) for no credit and no loot.</summary>
    Help,

    /// <summary>Nobody could be found behind the tag.</summary>
    NoTagger,

    /// <summary>The tagger is more than <see cref="PlayerbotContention.HelpYards"/> away.</summary>
    TooFar,

    /// <summary>The bot is at or below <see cref="PlayerbotContention.HelpHealthPct"/> of its health.</summary>
    BotHurt,

    /// <summary>A turn-in walk or a retreat is under way.</summary>
    Busy,

    /// <summary>The tagger is not losing: not under <see cref="PlayerbotContention.LosingHealthPct"/> of health and fewer than two enemies on them.</summary>
    TaggerWinning,

    /// <summary>The chance roll said no (or the chance is 0).</summary>
    Roll,
}

/// <summary>The facts of one help decision, gathered by the brain.</summary>
internal readonly record struct PlayerbotHelpFacts(bool TaggerKnown, float TaggerYards, float BotHealthPct, bool Busy, bool TaggerLosing,
    bool TaggerIsReal, bool SharesQuest);

/// <summary>
/// One bot's share of the contention rules: the mobs it left alone for being someone else's, its soft claim, the help it gives a
/// tagger, the quest creatures it set aside for gaining no credit, and the pure rules those use. World thread; owned by one brain; every
/// set is a small fixed array, so nothing allocates after construction.
/// </summary>
internal sealed class PlayerbotContention
{
    /// <summary>A tagger must be this near for the bot to help (yards).</summary>
    internal const float HelpYards = 30f;

    /// <summary>A bot helps only above this share of its own health (percent).</summary>
    internal const float HelpHealthPct = 60f;

    /// <summary>A tagger below this share of health counts as losing (percent).</summary>
    internal const float LosingHealthPct = 40f;

    /// <summary>A tagger with this many enemies on them counts as losing.</summary>
    internal const int LosingEnemies = 2;

    /// <summary>The widest stable per-(bot, mob) offset added to a mob's distance when a bot weighs targets (yards).</summary>
    internal const float JitterYards = 10f;

    /// <summary>A quest creature that gained no credit in this much contested hunting is set aside (<see cref="PlayerbotContentionOptions.ContentionSuspendMinutes"/>).</summary>
    internal const uint NoCreditMs = 180_000;

    private const int MaxSetAside = 8;
    private const int MaxSuspended = 4;

    private struct SetAside
    {
        public ulong Guid;
        public uint UntilMs;
    }

    private struct Suspended
    {
        public uint Entry;
        public uint UntilMs;
    }

    private readonly PlayerbotOptions _options;
    private readonly PlayerbotClaims _claims;
    private readonly SetAside[] _setAside = new SetAside[MaxSetAside];
    private readonly Suspended[] _suspended = new Suspended[MaxSuspended];
    private ulong _claimMob;
    private uint _claimEpoch;
    private uint _trackEntry;
    private uint _trackProgress;
    private uint _contestedMs;
    private uint _lastContestMs;

    internal PlayerbotContention(PlayerbotOptions options, PlayerbotClaims? claims = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _claims = claims ?? new PlayerbotClaims(64);
    }

    /// <summary>The registry the bot claims in (shared by the fleet).</summary>
    internal PlayerbotClaims Claims => _claims;

    /// <summary>The mob the bot is helping a tagger with (empty: none).</summary>
    internal ObjectGuid AssistMob { get; private set; }

    /// <summary>The player the bot is helping.</summary>
    internal ObjectGuid AssistTagger { get; private set; }

    /// <summary>The last help decision (tests and inspection).</summary>
    internal PlayerbotHelpDecision? LastDecision { get; private set; }

    /// <summary>The roll of a help decision, in [0, 1) (tests replace it; the default is a hash of the bot, the mob and the 15-second window).</summary>
    internal Func<ulong, ulong, uint, double>? RollOverride { get; set; }

    // --- pure rules ------------------------------------------------------------------------------------------------

    /// <summary>A 64-bit mix of two values (splitmix64's finaliser); stable across runs.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong a, ulong b)
    {
        unchecked
        {
            ulong z = (a ^ 0x9E3779B97F4A7C15UL) + (b * 0xBF58476D1CE4E5B9UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>How willing the bot is to help a stranger, 0..1, stable for a bot (a hash of its guid).</summary>
    internal static double Sociability(ulong botGuid) => (Mix(botGuid, 0x50C1AB1EUL) >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A stable offset in [0, <see cref="JitterYards"/>] for one bot and one mob, so equally near mobs are not ranked alike by every bot.</summary>
    internal static float Jitter(ulong botGuid, ulong mobGuid) => (Mix(botGuid, mobGuid) & 0xFFFF) * (JitterYards / 65535f);

    /// <summary>
    /// The chance to help: <paramref name="baseChance"/> times the bot's sociability, doubled when the tagger is a real player, raised by
    /// half when both share the kill quest; at most 1.
    /// </summary>
    internal static double HelpProbability(float baseChance, double sociability, bool taggerIsReal, bool sharesQuest)
        => Math.Min(1.0, baseChance * sociability * (taggerIsReal ? 2.0 : 1.0) * (sharesQuest ? 1.5 : 1.0));

    // --- someone else's mobs ----------------------------------------------------------------------------------------

    private uint MemoryMs => (uint)Math.Clamp(_options.Contention.SomeoneElsesMemorySeconds, 1, 600) * 1000u;

    /// <summary>Whether the bot left <paramref name="mob"/> alone as someone else's and the memory still holds.</summary>
    internal bool IsSetAside(ulong mob, uint nowMs)
    {
        SetAside[] set = _setAside;
        for (int i = 0; i < set.Length; i++)
            if (set[i].Guid == mob && unchecked((int)(set[i].UntilMs - nowMs)) > 0) return true;
        return false;
    }

    /// <summary>Leave <paramref name="mob"/> alone for <see cref="PlayerbotContentionOptions.SomeoneElsesMemorySeconds"/> (the oldest memory goes when all eight are held).</summary>
    internal void MarkSomeoneElses(ulong mob, uint nowMs)
    {
        if (mob == 0) return;
        SetAside[] set = _setAside;
        int slot = -1, oldest = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i].Guid == mob) { slot = i; break; }
            if (slot < 0 && unchecked((int)(set[i].UntilMs - nowMs)) <= 0) slot = i;
            if (unchecked((int)(set[i].UntilMs - set[oldest].UntilMs)) < 0) oldest = i;
        }

        if (slot < 0) slot = oldest;
        set[slot] = new SetAside { Guid = mob, UntilMs = unchecked(nowMs + MemoryMs) };
    }

    /// <summary>
    /// Whether <paramref name="creature"/> is no target for the bot now: left alone as someone else's for a while, or someone else's fight
    /// (<see cref="PlayerbotBrain.IsSomeoneElses"/>) that the bot has not chosen to help with.
    /// </summary>
    internal bool Blocks(Player player, Creature creature, ArcaneCore.Game.Groups.GroupManager? groups, uint nowMs)
    {
        bool blocked = IsSetAside(creature.Guid.Value, nowMs) || PlayerbotBrain.IsSomeoneElses(player, creature, groups);
        if (blocked) BlockedSeen++;
        return blocked;
    }

    // --- soft claims -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The weight of <paramref name="creature"/> as a target, lower is better: its distance, plus
    /// <see cref="PlayerbotContentionOptions.ClaimPenaltyYards"/> per other bot claiming it (an untagged mob only; the bot's own claim costs
    /// nothing), plus the stable per-(bot, mob) offset.
    /// </summary>
    internal float Weigh(ulong botGuid, Creature creature, float distance, uint nowMs)
    {
        ulong mob = creature.Guid.Value;
        float weight = distance + Jitter(botGuid, mob);
        float penalty = _options.Contention.ClaimPenaltyYards;
        if (penalty > 0f && creature.LootTapper.IsEmpty)
            weight += penalty * _claims.OthersOf(mob, _claimMob == mob, nowMs);
        return weight;
    }

    /// <summary>
    /// Claim <paramref name="creature"/> for this bot, or refresh the claim it holds (called when it is chosen and at each think while the
    /// bot walks to it). A tagged mob is no claim: the bot's claim ends.
    /// </summary>
    internal void Claim(ulong botGuid, Creature creature, uint nowMs)
    {
        ulong mob = creature.Guid.Value;
        if (!creature.LootTapper.IsEmpty || !creature.IsAlive) { ReleaseClaim(); return; }
        if (_claimMob == mob && _claims.Refresh(mob, _claimEpoch, nowMs)) return;
        ReleaseClaim();
        uint epoch = _claims.Join(mob, botGuid, nowMs);
        if (epoch == 0) return;
        _claimMob = mob;
        _claimEpoch = epoch;
    }

    /// <summary>End the bot's claim, if it holds one.</summary>
    internal void ReleaseClaim()
    {
        if (_claimMob == 0) return;
        _claims.Leave(_claimMob, _claimEpoch);
        _claimMob = 0;
        _claimEpoch = 0;
    }

    /// <summary>The mob the bot claims (0: none; tests).</summary>
    internal ulong ClaimedMob => _claimMob;

    // --- helping a tagger --------------------------------------------------------------------------------------------

    /// <summary>Whether the bot is helping with <paramref name="mob"/>.</summary>
    internal bool IsAssisting(ObjectGuid mob) => !AssistMob.IsEmpty && AssistMob == mob;

    internal void BeginAssist(ObjectGuid mob, ObjectGuid tagger)
    {
        AssistMob = mob;
        AssistTagger = tagger;
    }

    internal void EndAssist()
    {
        AssistMob = default;
        AssistTagger = default;
    }

    /// <summary>
    /// Help the tagger or leave the mob alone. Every condition must hold: the tagger known and within <see cref="HelpYards"/>, the bot
    /// above <see cref="HelpHealthPct"/> of its health with no turn-in or retreat pending, the tagger losing
    /// (<see cref="LosingHealthPct"/> or <see cref="LosingEnemies"/>), and the roll under <see cref="HelpProbability"/>.
    /// </summary>
    internal PlayerbotHelpDecision Decide(in PlayerbotHelpFacts facts, ulong botGuid, ulong mobGuid, uint nowMs)
    {
        PlayerbotHelpDecision decision = Judge(facts, botGuid, mobGuid, nowMs);
        LastDecision = decision;
        return decision;
    }

    private PlayerbotHelpDecision Judge(in PlayerbotHelpFacts facts, ulong botGuid, ulong mobGuid, uint nowMs)
    {
        if (!facts.TaggerKnown) return PlayerbotHelpDecision.NoTagger;
        if (!(facts.TaggerYards <= HelpYards)) return PlayerbotHelpDecision.TooFar;
        if (!(facts.BotHealthPct > HelpHealthPct)) return PlayerbotHelpDecision.BotHurt;
        if (facts.Busy) return PlayerbotHelpDecision.Busy;
        if (!facts.TaggerLosing) return PlayerbotHelpDecision.TaggerWinning;
        double chance = HelpProbability(_options.Contention.HelpChance, Sociability(botGuid), facts.TaggerIsReal, facts.SharesQuest);
        double roll = RollOverride is { } fixedRoll
            ? fixedRoll(botGuid, mobGuid, nowMs)
            : (Mix(Mix(botGuid, mobGuid), nowMs / MemoryMs) >> 11) * (1.0 / 9007199254740992.0);
        return roll < chance ? PlayerbotHelpDecision.Help : PlayerbotHelpDecision.Roll;
    }

    // --- quest creatures that give no credit ------------------------------------------------------------------------

    /// <summary>Whether the bot set the quest creature <paramref name="entry"/> aside for gaining no credit.</summary>
    internal bool IsEntrySuspended(uint entry, uint nowMs)
    {
        if (entry == 0) return false;
        Suspended[] set = _suspended;
        for (int i = 0; i < set.Length; i++)
            if (set[i].Entry == entry && unchecked((int)(set[i].UntilMs - nowMs)) > 0) return true;
        return false;
    }

    /// <summary>Set the quest creature <paramref name="entry"/> aside for <see cref="PlayerbotContentionOptions.ContentionSuspendMinutes"/>.</summary>
    internal void SuspendEntry(uint entry, uint nowMs)
    {
        if (entry == 0) return;
        uint ms = (uint)Math.Clamp(_options.Contention.ContentionSuspendMinutes, 1, 120) * 60_000u;
        Suspended[] set = _suspended;
        int slot = -1, oldest = 0;
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i].Entry == entry) { slot = i; break; }
            if (slot < 0 && unchecked((int)(set[i].UntilMs - nowMs)) <= 0) slot = i;
            if (unchecked((int)(set[i].UntilMs - set[oldest].UntilMs)) < 0) oldest = i;
        }

        if (slot < 0) slot = oldest;
        set[slot] = new Suspended { Entry = entry, UntilMs = unchecked(nowMs + ms) };
    }

    /// <summary>
    /// Watch the objective the bot hunts: <paramref name="progress"/> is the credit it holds for <paramref name="entry"/> (kills counted,
    /// or the quest item in the bags). A new entry or a rise resets the contested time. True, and the entry is set aside, when the bot
    /// spent <see cref="NoCreditMs"/> of contested hunting (<see cref="NoteContested"/>, <see cref="NoteLostMob"/>) without credit.
    /// Dying, resting and town visits add nothing, and a gap of over <see cref="ContestGapMs"/> pauses the clock; a bot that walks around the spawns while every creature of the entry is taken does accrue time.
    /// </summary>
    internal bool ObserveObjective(uint entry, uint progress, uint nowMs)
    {
        if (entry != _trackEntry)
        {
            _trackEntry = entry;
            _trackProgress = progress;
            _contestedMs = 0;
            _lastContestMs = 0;
            return false;
        }

        if (progress > _trackProgress) { _contestedMs = 0; _lastContestMs = 0; }
        _trackProgress = progress;
        if (_contestedMs < NoCreditMs) return false;
        SuspendEntry(entry, nowMs);
        _trackEntry = 0;
        _contestedMs = 0;
        return true;
    }

    /// <summary>The longest gap between two contested observations that still counts as continuous hunting; a longer one adds nothing (the clock pauses).</summary>
    internal const uint ContestGapMs = 5_000;

    /// <summary>The contested time one lost mob (left alone as another's, or a stolen kill) adds.</summary>
    internal const uint LostMobMs = 20_000;

    /// <summary>
    /// The bot looked for <paramref name="entry"/> and could take none: at least one creature of it is in sight and every one is taken by others (tagged, or left alone as
    /// someone else's). Adds the time since the last such look when it is at most <see cref="ContestGapMs"/>: a gap (a walk, a death, a
    /// town visit) adds nothing.
    /// </summary>
    internal void NoteContested(uint entry, uint nowMs)
    {
        if (entry == 0 || entry != _trackEntry) return;
        uint gap = unchecked(nowMs - _lastContestMs);
        if (_lastContestMs != 0 && gap <= ContestGapMs) _contestedMs += gap;
        _lastContestMs = nowMs == 0 ? 1 : nowMs;
    }

    /// <summary>A creature of <paramref name="entry"/> went to someone else (left alone as another's, or its kill was stolen).</summary>
    internal void NoteLostMob(uint entry)
    {
        if (entry != 0 && entry == _trackEntry) _contestedMs += LostMobMs;
    }

    /// <summary>The contested hunting time of the objective watched (tests).</summary>
    internal uint ContestedMs => _contestedMs;

    /// <summary>Candidates the last target choice skipped as taken by others (<see cref="Blocks"/>); the brain resets it before each choice.</summary>
    internal int BlockedSeen { get; set; }

    /// <summary>Forget the objective being watched (none is hunted).</summary>
    internal void ClearObjective() { _trackEntry = 0; _contestedMs = 0; }
}

/// <summary>
/// The creatures that drop each item, read from the loot tables once per loot content (<see cref="PlayerbotGroupContent.DropIndex"/>):
/// what lets a bot hunt a quest's item objective on purpose. The cache lives as long as the loot content object (a reload replaces it).
/// </summary>
internal static class PlayerbotDropIndex
{
    private static readonly ConditionalWeakTable<LootContent, Dictionary<uint, uint[]>> Cache = new();

    /// <summary>The creature entries that drop <paramref name="item"/>, ascending (empty when none does).</summary>
    internal static IReadOnlyList<uint> DroppersOf(LootContent loot, uint item)
        => Cache.GetValue(loot, PlayerbotGroupContent.DropIndex).GetValueOrDefault(item) ?? [];
}

/// <summary>One candidate target and its weight (lower is better; <see cref="PlayerbotBrain.CollectTargets"/>).</summary>
internal struct PlayerbotWeighedTarget
{
    internal Creature? Creature;
    internal float Weight;
}

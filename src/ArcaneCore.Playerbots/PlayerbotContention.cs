using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.World.Playerbots.Groups;
using System.Runtime.CompilerServices;

namespace ArcaneCore.World.Playerbots;

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
    private ulong _lastCreditedTapGuid;

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
        if (_trackEntry != 0 && creature.Entry == _trackEntry && !creature.LootTapper.IsEmpty
            && creature.Guid.Value != _lastCreditedTapGuid && PlayerbotBrain.HasLootRights(player, creature))
        {
            _lastCreditedTapGuid = creature.Guid.Value;
            NoteCreditedKill(_trackEntry);
        }

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
    internal bool ObserveObjective(uint entry, uint progress, uint nowMs, Player? player = null)
    {
        if (entry != _trackEntry)
        {
            _trackEntry = entry;
            _trackProgress = progress;
            _contestedMs = 0;
            _lastContestMs = 0;
            _lastCreditedTapGuid = 0;
            return false;
        }

        if (player is not null && HasActiveOrTappedCreature(player, entry))
        {
            _contestedMs = 0;
            _lastContestMs = 0;
        }

        if (progress > _trackProgress) { _contestedMs = 0; _lastContestMs = 0; }
        _trackProgress = progress;
        if (_contestedMs < NoCreditMs) return false;
        SuspendEntry(entry, nowMs);
        _trackEntry = 0;
        _contestedMs = 0;
        return true;
    }

    /// <summary>Reset the contested hunting clock when the bot kills or loots a creature of <paramref name="entry"/>, even if an RNG drop did not roll.</summary>
    public void NoteCreditedKill(uint entry)
    {
        if (entry != 0 && entry == _trackEntry)
        {
            _contestedMs = 0;
            _lastContestMs = 0;
        }
    }

    private bool HasActiveOrTappedCreature(Player player, uint entry)
    {
        if (player.Combat.Victim is Creature { Entry: var targetEntry } && targetEntry == entry)
        {
            return true;
        }

        if (player.Map is not { } map)
        {
            return false;
        }

        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (guid.High == HighGuid.Unit
                && guid.Entry == entry
                && map.FindObject(guid) is Creature creature
                && !creature.LootTapper.IsEmpty
                && creature.Guid.Value != _lastCreditedTapGuid
                && PlayerbotBrain.HasLootRights(player, creature))
            {
                _lastCreditedTapGuid = creature.Guid.Value;
                return true;
            }
        }

        return false;
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
    private static readonly ConditionalWeakTable<LootContent, Dictionary<uint, uint[]>> HuntCache = new();

    /// <summary>
    /// A dropper whose chance for an item, times this, is still below the best dropper's chance for it drops the item only as a trace
    /// (<see cref="HuntIndex"/>): Small Crag Boar has Crag Boar Rib at 0.02% where Crag Boar has it at 52%.
    /// </summary>
    internal const float TraceDropRatio = 20f;

    /// <summary>The whole drop index of <paramref name="loot"/> (item entry to its droppers, ascending): the cache <see cref="DroppersOf"/> reads. Read only.</summary>
    internal static IReadOnlyDictionary<uint, uint[]> Index(LootContent loot)
        => Cache.GetValue(loot, PlayerbotGroupContent.DropIndex);

    /// <summary>The creature entries that drop <paramref name="item"/>, ascending (empty when none does).</summary>
    internal static IReadOnlyList<uint> DroppersOf(LootContent loot, uint item)
        => Cache.GetValue(loot, PlayerbotGroupContent.DropIndex).GetValueOrDefault(item) ?? [];

    /// <summary>
    /// The creature entries worth hunting <paramref name="item"/> at, ascending: <see cref="DroppersOf"/> less the trace droppers
    /// (<see cref="HuntIndex"/>). Empty when none drops it.
    /// </summary>
    internal static IReadOnlyList<uint> HuntedDroppersOf(LootContent loot, uint item)
        => HuntCache.GetValue(loot, HuntIndex).GetValueOrDefault(item) ?? [];

    /// <summary>
    /// Which creatures drop each item at a chance worth hunting it for (item entry to creature entries, ascending). A creature's chance for
    /// an item is the best of its rows that yield it (<see cref="DropChances"/>); a dropper whose chance times <see cref="TraceDropRatio"/>
    /// is below the best dropper's chance is left out. The world data carries such traces from sampled drop rates (Small Crag Boar:
    /// Crag Boar Rib 0.02%, Chunk of Boar Meat 0.0094%; Crag Boar 52%, Dire Mottled Boar 98%): hunting one gains no credit, and a bot
    /// too low for every other dropper hunted it until the contention clock set it aside (GitHub #809, <c>reason=no-credit-timeout</c>).
    /// </summary>
    internal static Dictionary<uint, uint[]> HuntIndex(LootContent loot)
    {
        ArgumentNullException.ThrowIfNull(loot);
        var chances = new Dictionary<uint, List<(uint Creature, float Chance)>>();
        var references = new Dictionary<uint, Dictionary<uint, float>>();
        foreach (CreatureLootInfo creature in loot.CreatureInfos)
        {
            if (creature.LootId == 0) continue;
            foreach ((uint item, float chance) in DropChances(loot, LootTableKind.Creature, creature.LootId, 0, references))
            {
                if (!chances.TryGetValue(item, out List<(uint, float)>? droppers)) chances[item] = droppers = [];
                droppers.Add((creature.Entry, chance));
            }
        }

        var index = new Dictionary<uint, uint[]>(chances.Count);
        foreach ((uint item, List<(uint Creature, float Chance)> droppers) in chances)
        {
            float best = 0;
            foreach ((_, float chance) in droppers) best = Math.Max(best, chance);
            index[item] = [.. droppers.Where(d => d.Chance * TraceDropRatio >= best).Select(d => d.Creature).Order()];
        }

        return index;
    }

    /// <summary>
    /// The percent chance one loot of table <paramref name="entry"/> yields each item, the best row's where several do. The chance is
    /// the row's absolute ChanceOrQuestChance (vmangos <c>LootStoreItem</c> keeps <c>fabs</c> of it, LootMgr.h:119-121; a negative one
    /// only marks a quest drop), capped at 100 (LootMgr.cpp:258). An equal-chanced row of a group (chance 0) gets an equal share of what
    /// the group's explicit chances leave (vmangos <c>LootGroup::Roll</c>, LootMgr.cpp:1056-1093: the explicit chances first, then one
    /// equal-chanced row); an ungrouped one never rolls. A reference row scales the chances of the table it names by its own (LootMgr.cpp:261-262, 1220-1235), nested up to
    /// <see cref="PlayerbotGroupContent.MaxReferenceDepth"/>; a reference cycle ends at the table already being read.
    /// </summary>
    private static Dictionary<uint, float> DropChances(LootContent loot, LootTableKind kind, uint entry, int depth,
        Dictionary<uint, Dictionary<uint, float>> references)
    {
        if (kind == LootTableKind.Reference && references.TryGetValue(entry, out Dictionary<uint, float>? known)) return known;
        var chances = new Dictionary<uint, float>();
        if (kind == LootTableKind.Reference) references[entry] = chances;
        IReadOnlyList<LootStoreRow> rows = loot.GetRows(kind, entry);
        var explicitByGroup = new Dictionary<byte, float>();
        var equalByGroup = new Dictionary<byte, int>();
        foreach (LootStoreRow row in rows)
        {
            if (row.GroupId == 0) continue;
            if (row.ChanceOrQuestChance == 0)
                equalByGroup[row.GroupId] = equalByGroup.GetValueOrDefault(row.GroupId) + 1;
            else
                explicitByGroup[row.GroupId] = explicitByGroup.GetValueOrDefault(row.GroupId) + Math.Abs(row.ChanceOrQuestChance);
        }

        foreach (LootStoreRow row in rows)
        {
            float chance = Math.Min(Math.Abs(row.ChanceOrQuestChance), 100f);
            if (chance == 0 && row.GroupId != 0)
                chance = Math.Max(0f, 100f - explicitByGroup.GetValueOrDefault(row.GroupId)) / equalByGroup[row.GroupId];
            if (chance <= 0) continue;
            if (row.MinCountOrRef < 0)
            {
                if (depth >= PlayerbotGroupContent.MaxReferenceDepth) continue;
                foreach ((uint item, float inner) in DropChances(loot, LootTableKind.Reference, (uint)-row.MinCountOrRef, depth + 1, references))
                    Keep(chances, item, chance * inner / 100f);
            }
            else if (row.Item != 0)
            {
                Keep(chances, row.Item, chance);
            }
        }

        return chances;

        static void Keep(Dictionary<uint, float> chances, uint item, float chance)
            => chances[item] = Math.Max(chances.GetValueOrDefault(item), chance);
    }
}

/// <summary>One candidate target and its weight (lower is better; <see cref="PlayerbotBrain.CollectTargets"/>).</summary>
internal struct PlayerbotWeighedTarget
{
    internal Creature? Creature;
    internal float Weight;
}

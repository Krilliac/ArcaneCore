using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Talents;

/// <summary>
/// The talent system on the world thread: free-point accounting, learning, load normalisation and resets. It hangs on
/// the spell system through <see cref="ISpellLearnObserver"/> (rank replacement and point bookkeeping happen for every
/// talent spell learn, wherever it comes from: CMSG_LEARN_TALENT, a GM command, a spell effect) and keeps no state on
/// <see cref="Player"/>. Talent <em>effects</em> (spell modifiers, procs, stat auras) are the spell system's: this
/// service only decides which rank spells a character knows (docs/areas/talents.md).
/// </summary>
public sealed partial class TalentService : IDisposable
{
    private readonly ConditionalWeakTable<Player, PlayerTalentState> _states = new();
    private readonly Observer _observer;
    private readonly Func<long> _unixNow;
    private readonly ILogger _logger;

    public TalentService(TalentCatalog catalog, SpellSystem spells, TalentOptions options, Func<long>? unixNow = null, ILogger? logger = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Spells = spells ?? throw new ArgumentNullException(nameof(spells));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _logger = logger ?? NullLogger.Instance;
        _observer = new Observer(this);
        Spells.AddLearnObserver(_observer);
    }

    public TalentCatalog Catalog { get; }

    public SpellSystem Spells { get; }

    public TalentOptions Options { get; }

    /// <summary>Spell rank links, to find the higher ranks a respec must disable. Null: ranks are not followed (only the talents are removed).</summary>
    public IRankChain? RankChain { get; set; }

    /// <summary>Enumerates a player's known spells (the book itself only answers HasSpell). Null: higher ranks are not followed.</summary>
    public Func<Player, IEnumerable<uint>>? KnownSpells { get; set; }

    /// <summary>Persistence of the respec economy and the disabled set. Null: changes stay in memory.</summary>
    public ITalentSink? Sink { get; set; }

    /// <summary>
    /// Raised after a talent rank spell was learned by any route (the pets lane re-casts the owner's talent auras on the
    /// pet here: mangos-classic SkillHandler.cpp:34).
    /// </summary>
    public event Action<Player>? TalentLearned;

    /// <summary>
    /// Raised after a successful <see cref="ResetTalents"/> (the pets lane removes the hunter pet here: vmangos
    /// Player.cpp:4146 RemovePet(PET_SAVE_REAGENTS)).
    /// </summary>
    public event Action<Player>? TalentsReset;

    /// <summary>The talent state of a player (created empty on first use).</summary>
    public PlayerTalentState StateOf(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return _states.GetValue(player, _ => new PlayerTalentState());
    }

    /// <summary>PLAYER_CHARACTER_POINTS1: the unspent talent points the client shows.</summary>
    public uint FreePoints(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.GetUInt32(UpdateFields.PlayerCharacterPoints1);
    }

    /// <summary>The points spent: the sum of rank costs over the known talent rank spells (vmangos m_usedTalentCount).</summary>
    public uint UsedPoints(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return TalentRules.UsedPoints(Catalog, spell => HasSpell(player, spell));
    }

    public bool HasSpell(Player player, uint spellId) => Spells.Spellbook?.HasSpell(player, spellId) ?? false;

    /// <summary>
    /// vmangos Player::InitTalentForLevel (Player.cpp:3249-3252) = UpdateFreeTalentPoints(true): call at login after the
    /// spellbook is loaded, after every level change, and from GM/template code that sets a level directly.
    /// </summary>
    public void InitTalentForLevel(Player player) => UpdateFreeTalentPoints(player, resetIfNeed: true);

    /// <summary>
    /// vmangos Player::UpdateFreeTalentPoints (Player.cpp:3217-3247): recompute PLAYER_CHARACTER_POINTS1 from the level
    /// and the spent points; an overspend resets the talents of a non-administrator when <paramref name="resetIfNeed"/>.
    /// </summary>
    public void UpdateFreeTalentPoints(Player player, bool resetIfNeed)
    {
        ArgumentNullException.ThrowIfNull(player);
        TalentFreePointsDecision decision = TalentRules.DecideFreePoints(
            player.Level, UsedPoints(player), Options.PointsRate, resetIfNeed, player.Security >= AccountSecurity.Administrator);
        if (decision.Reset)
        {
            ResetTalents(player, noCost: true);
        }

        if (decision.FreePoints is uint free)
        {
            player.SetUInt32(UpdateFields.PlayerCharacterPoints1, free);
        }
    }

    /// <summary>
    /// CMSG_LEARN_TALENT (vmangos Player::LearnTalent, Player.cpp:20684-20800; no reply packet on success or refusal).
    /// <paramref name="rankIndex"/> is the zero-based requested rank. The spell goes through
    /// <see cref="SpellSystem.LearnSpell"/>, whose observer replaces the old rank and updates the points.
    /// </summary>
    public bool LearnTalent(Player player, uint talentId, uint rankIndex)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (!player.CanMutateQuestSettlementState || Spells.Spellbook is null)
        {
            return false;
        }

        uint classMask = ClassMask(player);
        TalentLearnResult result = TalentRules.EvaluateLearn(
            Catalog, spell => HasSpell(player, spell), talentId, rankIndex, FreePoints(player), classMask);
        if (result.Outcome != TalentLearnOutcome.Ok)
        {
            _logger.LogDebug("{Player} talent {Talent} rank {Rank} refused: {Outcome}", player.Name, talentId, rankIndex, result.Outcome);
            return false;
        }

        return Spells.LearnSpell(player, result.RankSpell);
    }

    /// <summary>
    /// Login normalisation, silent (the client is not in the world yet): where the spellbook holds several ranks of one
    /// talent only the highest is kept, as vmangos AddSpell removes the other ranks while spells load
    /// (Player.cpp:3606-3620). Returns the dropped spells so the caller can persist the removals.
    /// </summary>
    public IReadOnlyList<uint> RemoveSupersededRanks(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Spells.Spellbook is not { } book)
        {
            return [];
        }

        List<uint> dropped = [];
        foreach (TalentRecord talent in Catalog.Talents)
        {
            int highest = TalentRules.HighestKnownRank(talent, spell => book.HasSpell(player, spell));
            for (int rank = 0; rank < highest - 1; rank++)
            {
                uint spell = talent.RankSpells[rank];
                if (spell != 0 && book.HasSpell(player, spell) && book.ForgetSpell(player, spell))
                {
                    dropped.Add(spell);
                }
            }
        }

        return dropped;
    }

    public void Dispose() => Spells.RemoveLearnObserver(_observer);

    /// <summary>The player's class as a mask (vmangos Player::GetClassMask = 1 &lt;&lt; (class - 1)).</summary>
    internal static uint ClassMask(Player player) => 1u << ((int)player.Class - 1);

    /// <summary>
    /// The spell-system hook: replaces other ranks of a talent on learn (Player.cpp:3606-3620) and keeps the free-point
    /// field current (:3697-3700, :3857), and casts LEARN_SPELL talents ignoring their stance (:3709-3716).
    /// </summary>
    private sealed class Observer(TalentService owner) : ISpellLearnObserver
    {
        public bool BeforeLearn(Player player, uint spellId)
        {
            if (!owner.Catalog.TryGetRankPosition(spellId, out TalentRankPosition position) || owner.HasSpell(player, spellId))
            {
                return true;
            }

            foreach (uint other in owner.Catalog.ById(position.TalentId)!.RankSpells)
            {
                if (other != 0 && other != spellId && owner.HasSpell(player, other) && !owner.Spells.RemoveSpell(player, other))
                {
                    return false;
                }
            }

            return true;
        }

        public void AfterLearn(Player player, uint spellId)
        {
            owner.ClearDisabled(player, spellId);   // any learn of a hidden spell un-hides it (vmangos AddSpell, Player.cpp:3560-3585)
            if (!owner.Catalog.TryGetRankPosition(spellId, out _))
            {
                return;
            }

            owner.UpdateFreeTalentPoints(player, resetIfNeed: false);
            if (owner.Spells.Store.Get(spellId) is { IsPassive: false } spell && spell.HasEffect(SpellEffectName.LearnSpell))
            {
                // vmangos: "ignore stance requirement for talent learn spell" - a triggered cast.
                owner.Spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);
            }

            owner.ReEnableHigherRanks(player, spellId);
            owner.TalentLearned?.Invoke(player);
        }

        public void AfterRemove(Player player, uint spellId)
        {
            if (owner.Catalog.TryGetRankPosition(spellId, out _))
            {
                owner.UpdateFreeTalentPoints(player, resetIfNeed: false);
            }
        }
    }
}

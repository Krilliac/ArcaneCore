using System.Globalization;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>
/// A bot's usable class abilities, resolved from its spellbook (vmangos CombatBotBaseAI::PopulateSpellData,
/// CombatBotBaseAI.cpp:136-1827): every wanted ability by its Spell.dbc name at the highest known rank, plus the direct and
/// periodic single-target heal lists (strongest first, as the vmangos heal sets order them). Passive and hidden spells are
/// ignored. Names match whole (vmangos matches substrings, which also catches "Judgement of ..." for "Judgement"); the
/// rotations name each ability exactly. Immutable: a learned or superseded spell rebuilds a new instance.
/// </summary>
internal sealed class PlayerbotAbilities
{
    private readonly Dictionary<string, SpellInfo> _byName;

    private PlayerbotAbilities(Dictionary<string, SpellInfo> byName, IReadOnlyList<SpellInfo> directHeals,
        IReadOnlyList<SpellInfo> periodicHeals, IReadOnlySet<uint> known)
    {
        _byName = byName;
        DirectHeals = directHeals;
        PeriodicHeals = periodicHeals;
        Known = known;
    }

    internal static PlayerbotAbilities Empty { get; } = new([], [], [], new HashSet<uint>());

    /// <summary>Single-target direct heals (SPELL_EFFECT_HEAL), strongest base points first.</summary>
    internal IReadOnlyList<SpellInfo> DirectHeals { get; }

    /// <summary>Single-target heal-over-time auras (SPELL_AURA_PERIODIC_HEAL), strongest first.</summary>
    internal IReadOnlyList<SpellInfo> PeriodicHeals { get; }

    /// <summary>Every spell id the book held when this was built (vmangos Player::HasSpell for the role table).</summary>
    internal IReadOnlySet<uint> Known { get; }

    internal int Count => _byName.Count;

    /// <summary>The highest known rank of <paramref name="name"/>, or null.</summary>
    internal SpellInfo? this[string name] => _byName.GetValueOrDefault(name);

    internal bool Has(string name) => _byName.ContainsKey(name);

    /// <summary>Whether the book holds <paramref name="spellId"/> or any rank of the spell of that id's name.</summary>
    internal bool HasSpellOrRank(uint spellId, Func<uint, SpellInfo?> lookup)
        => Known.Contains(spellId) || lookup(spellId) is { } spell && Known.Any(id => lookup(id)?.Name == spell.Name);

    internal static PlayerbotAbilities Resolve(IEnumerable<SpellInfo> known, IEnumerable<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(wanted);
        var names = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
        var direct = new List<SpellInfo>();
        var periodic = new List<SpellInfo>();
        var ids = new HashSet<uint>();
        foreach (SpellInfo spell in known)
        {
            ids.Add(spell.Id);
            // vmangos skips SPELL_ATTR_PASSIVE and SPELL_ATTR_DO_NOT_DISPLAY (CombatBotBaseAI.cpp:211-215).
            if (spell.IsPassive || spell.HasAttribute(SpellAttributes.DoNotDisplay)) continue;
            if (names.Contains(spell.Name) && (!byName.TryGetValue(spell.Name, out SpellInfo? current) || IsHigherRank(spell, current)))
                byName[spell.Name] = spell;
            if (IsSingleTargetHeal(spell, SpellEffectName.Heal, AuraType.None)) direct.Add(spell);
            else if (IsSingleTargetHeal(spell, SpellEffectName.ApplyAura, AuraType.PeriodicHeal)) periodic.Add(spell);
        }

        direct.Sort((a, b) => HealAmount(b).CompareTo(HealAmount(a)));
        periodic.Sort((a, b) => HealAmount(b).CompareTo(HealAmount(a)));
        return new PlayerbotAbilities(byName, direct, periodic, ids);
    }

    /// <summary>vmangos IsHigherRankSpell (CombatBotBaseAI.cpp:217-227): by "Rank N" when both carry one, else by id.</summary>
    internal static bool IsHigherRank(SpellInfo candidate, SpellInfo current)
    {
        int newRank = RankOf(candidate);
        return newRank != 0 ? newRank > RankOf(current) : candidate.Id > current.Id;
    }

    internal static int RankOf(SpellInfo spell)
    {
        string rank = spell.Rank;
        int space = rank.LastIndexOf(' ');
        return rank.StartsWith("Rank", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(rank.AsSpan(space + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }

    /// <summary>
    /// The amount a heal restores, as vmangos SelectMostEfficientHealingSpell counts it (CombatBotBaseAI.cpp:1897-1915): the
    /// heal effects' base points, and for a heal-over-time aura the ticks over its duration.
    /// </summary>
    internal static int HealAmount(SpellInfo spell)
    {
        int amount = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            int points = effect.BasePoints + Math.Max(1, effect.BaseDice);
            if (effect.Effect == SpellEffectName.Heal) amount += points;
            else if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType == AuraType.PeriodicHeal && effect.Amplitude > 0)
                amount += Math.Max(1, spell.GetDuration() / (int)effect.Amplitude) * points;
        }
        return amount;
    }

    private static bool IsSingleTargetHeal(SpellInfo spell, SpellEffectName effectName, AuraType aura)
    {
        if (spell.IsChanneled || spell.PowerType == Game.Spells.SpellMath.PowerHealth) return false;
        bool found = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty) continue;
            if (effect.Radius > 0) return false;
            if (effect.Effect == effectName && (aura == AuraType.None || effect.AuraType == aura)
                && effect.TargetA is SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitCaster
                    or SpellImplicitTarget.UnitFriendAndParty)
                found = true;
        }
        return found;
    }
}

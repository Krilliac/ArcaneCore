using System.Globalization;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>Who a heal can land on, from the target selectors of the effects that heal (Spell.dbc EffectImplicitTargetA).</summary>
internal enum PlayerbotHealReach
{
    /// <summary>Not a heal, or one the bot cannot aim (no usable target selector).</summary>
    None,

    /// <summary>Lands on the caster whatever is targeted (Desperate Prayer: TARGET_UNIT_CASTER).</summary>
    SelfOnly,

    /// <summary>A group heal centred on the caster, or one only a group member can receive (Prayer of Healing, Holy Nova): never aimed at one unit.</summary>
    Party,

    /// <summary>One friendly unit of the caster's choice (TARGET_UNIT_FRIEND, TARGET_UNIT; Holy Light, Flash Heal, Lesser Healing Wave).</summary>
    Friend,

    /// <summary>One friendly unit, then jumps to hurt friends nearby (TARGET_UNIT_FRIEND_CHAIN_HEAL; Healing Wave, Chain Heal).</summary>
    ChainFriend,
}

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

    private readonly Dictionary<uint, (PlayerbotHealReach Reach, int Amount)> _heals;

    private PlayerbotAbilities(Dictionary<string, SpellInfo> byName, IReadOnlyList<SpellInfo> directHeals,
        IReadOnlyList<SpellInfo> periodicHeals, IReadOnlySet<uint> known, Dictionary<uint, (PlayerbotHealReach, int)> heals)
    {
        _byName = byName;
        DirectHeals = directHeals;
        PeriodicHeals = periodicHeals;
        Known = known;
        _heals = heals;
    }

    internal static PlayerbotAbilities Empty { get; } = new([], [], [], new HashSet<uint>(), []);

    /// <summary>
    /// Direct heals a bot can aim at one unit (see <see cref="ClassifyHeal"/>: a heal effect, or a script or trigger effect whose heal is
    /// another spell; the caster-only ones included, group heals not), strongest first.
    /// </summary>
    internal IReadOnlyList<SpellInfo> DirectHeals { get; }

    /// <summary>Who the direct or periodic heal <paramref name="spell"/> (a member of <see cref="DirectHeals"/> or <see cref="PeriodicHeals"/>) lands on.</summary>
    internal PlayerbotHealReach HealReachOf(SpellInfo spell) => _heals.TryGetValue(spell.Id, out var heal) ? heal.Reach : PlayerbotHealReach.None;

    /// <summary>Whether <paramref name="spell"/> is a listed heal that lands on a unit other than its caster.</summary>
    internal bool HealsAnotherUnit(SpellInfo spell) => HealReachOf(spell) is PlayerbotHealReach.Friend or PlayerbotHealReach.ChainFriend;

    /// <summary><see cref="HealAmount(SpellInfo)"/>, and for a listed heal whose heal is another spell the amount of that heal.</summary>
    internal int HealAmountOf(SpellInfo spell) => _heals.TryGetValue(spell.Id, out var heal) ? heal.Amount : HealAmount(spell);

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

    /// <param name="store">The spell data a script effect's heal is read from; null classifies each spell on its own effects.</param>
    internal static PlayerbotAbilities Resolve(IEnumerable<SpellInfo> known, IEnumerable<string> wanted, SpellStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(wanted);
        var names = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, SpellInfo>(StringComparer.OrdinalIgnoreCase);
        var direct = new List<SpellInfo>();
        var periodic = new List<SpellInfo>();
        var ids = new HashSet<uint>();
        var heals = new Dictionary<uint, (PlayerbotHealReach, int)>();
        foreach (SpellInfo spell in known)
        {
            ids.Add(spell.Id);
            // vmangos skips SPELL_ATTR_PASSIVE and SPELL_ATTR_DO_NOT_DISPLAY (CombatBotBaseAI.cpp:211-215).
            if (spell.IsPassive || spell.HasAttribute(SpellAttributes.DoNotDisplay)) continue;
            if (names.Contains(spell.Name) && (!byName.TryGetValue(spell.Name, out SpellInfo? current) || IsHigherRank(spell, current)))
                byName[spell.Name] = spell;
            if (ClassifyHeal(spell, AuraType.None, store) is { Reach: not PlayerbotHealReach.Party } directHeal)
            {
                direct.Add(spell);
                heals[spell.Id] = directHeal;
            }
            else if (ClassifyHeal(spell, AuraType.PeriodicHeal, store) is { Reach: not PlayerbotHealReach.Party } periodicHeal)
            {
                periodic.Add(spell);
                heals[spell.Id] = periodicHeal;
            }
        }

        direct.Sort((a, b) => heals[b.Id].Item2.CompareTo(heals[a.Id].Item2));
        periodic.Sort((a, b) => HealAmount(b).CompareTo(HealAmount(a)));
        return new PlayerbotAbilities(byName, direct, periodic, ids, heals);
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

    /// <summary>
    /// Whether <paramref name="spell"/> heals (<paramref name="aura"/> None: directly; PeriodicHeal: over time), who it can land on, and
    /// how much a cast restores; null when it does not heal. A direct heal is an effect Heal, or a ScriptEffect (Holy Light, Flash of
    /// Light: the script casts the rank-less sibling spell of the same name, family and icon that carries the Heal effect, with this
    /// effect's value) or TriggerSpell effect whose triggered spell has a Heal effect. The reach comes from the healing effect's
    /// TargetA; any area effect on the spell (a radius) makes it a <see cref="PlayerbotHealReach.Party"/> heal.
    /// </summary>
    internal static (PlayerbotHealReach Reach, int Amount)? ClassifyHeal(SpellInfo spell, AuraType aura, SpellStore? store)
    {
        if (spell.IsChanneled || spell.PowerType == Game.Spells.SpellMath.PowerHealth) return null;
        bool healing = false, area = false;
        PlayerbotHealReach reach = PlayerbotHealReach.None;
        int amount = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.IsEmpty) continue;
            if (effect.Radius > 0) area = true;
            int points = effect.BasePoints + Math.Max(1, effect.BaseDice);
            int effectAmount;
            if (aura == AuraType.None)
            {
                if (effect.Effect == SpellEffectName.Heal) effectAmount = 0; // counted by HealAmount below
                else if (effect.Effect == SpellEffectName.ScriptEffect && ReachOf(effect.TargetA) != PlayerbotHealReach.None
                    && ScriptedHeal(spell, store) is not null) effectAmount = points;
                else if (effect.Effect == SpellEffectName.TriggerSpell && store?.Get(effect.TriggerSpell) is { } triggered
                    && triggered.HasEffect(SpellEffectName.Heal)) effectAmount = HealAmount(triggered);
                else continue;
            }
            else if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType == aura && effect.Amplitude > 0)
                effectAmount = Math.Max(1, spell.GetDuration() / (int)effect.Amplitude) * points;
            else continue;

            healing = true;
            amount += effectAmount;
            PlayerbotHealReach effectReach = ReachOf(effect.TargetA);
            if (Strength(effectReach) > Strength(reach)) reach = effectReach;
        }

        if (!healing || reach == PlayerbotHealReach.None) return null;
        return (area ? PlayerbotHealReach.Party : reach, aura == AuraType.None ? HealAmount(spell) + amount : amount);
    }

    /// <summary>Who a heal effect with target selector <paramref name="target"/> lands on.</summary>
    internal static PlayerbotHealReach ReachOf(SpellImplicitTarget target) => target switch
    {
        SpellImplicitTarget.UnitCaster => PlayerbotHealReach.SelfOnly,
        SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriendAndParty => PlayerbotHealReach.Friend,
        SpellImplicitTarget.UnitFriendChainHeal => PlayerbotHealReach.ChainFriend,
        SpellImplicitTarget.UnitParty or SpellImplicitTarget.UnitRaid or SpellImplicitTarget.EnumUnitsPartyWithinCasterRange
            or SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc
            or SpellImplicitTarget.EnumUnitsRaidWithinCasterRange or SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc
            or SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc => PlayerbotHealReach.Party,
        _ => PlayerbotHealReach.None,
    };

    private static int Strength(PlayerbotHealReach reach) => reach switch
    {
        PlayerbotHealReach.SelfOnly => 1,
        PlayerbotHealReach.Party => 2,
        PlayerbotHealReach.Friend => 3,
        PlayerbotHealReach.ChainFriend => 4,
        _ => 0,
    };

    /// <summary>The heal a ScriptEffect spell casts: its rank-less sibling of the same name, family and icon that has a Heal effect (Holy Light 635 and 19968).</summary>
    private static SpellInfo? ScriptedHeal(SpellInfo spell, SpellStore? store)
    {
        if (store is null || spell.Rank.Length == 0) return null;
        foreach (SpellInfo other in store.All)
        {
            if (other.Id != spell.Id && other.Rank.Length == 0 && other.SpellFamilyName == spell.SpellFamilyName
                && other.SpellIconId == spell.SpellIconId && string.Equals(other.Name, spell.Name, StringComparison.Ordinal)
                && other.HasEffect(SpellEffectName.Heal)) return other;
        }

        return null;
    }
}

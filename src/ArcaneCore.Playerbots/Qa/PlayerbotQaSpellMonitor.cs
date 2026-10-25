using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Watches the spells of managed playerbots and reports what does not work (docs/areas/playbots-qa.md, Spell signals), straight to
/// <see cref="IPlayerbotQaSink"/> with no log line:
/// <list type="bullet">
/// <item><see cref="QaSignalKind.SpellUnsupported"/>: a bot's own (not triggered) cast that really met a missing effect handler, an Unsupported aura type or an
/// implicit target the system cannot select (<see cref="OnUnsupported"/>, raised on the failing path itself, so a handler the live server has can never be
/// reported), and a cast of a spell with a silent gap the cast cannot show (a DUMMY or SCRIPT_EFFECT no script claims, a DUMMY aura nothing reads:
/// <see cref="QaSpellGaps.IsSilent"/>, from <see cref="PlayerbotQaSpellGapAnalyzer"/>). Triggered casts (procs, item and script casts) are skipped.</item>
/// <item><see cref="QaSignalKind.SpellRefused"/>: the cast result the server refused a bot's cast with (<see cref="OnCastRefused"/>): a suspicious
/// result at once, an expected one when the same bot gets it <see cref="RepeatThreshold"/> times within <see cref="RepeatWindowMs"/>
/// (<see cref="PlayerbotQaSpellRefusals"/>).</item>
/// <item><see cref="QaSignalKind.SpellNoEffect"/>: <see cref="NoEffectRepeats"/> hits in a row of one bot's spell that should deal damage, heal or leave an
/// aura and did none of it (misses, resists and immunities are no hit; overhealing a full target counts as working).</item>
/// </list>
/// Every callback first asks whether the caster is a managed playerbot (<see cref="PlayerbotQaBots.Identify"/>, two type checks); a real player or a
/// creature returns there, so nothing a real player does is reported. The common path of a bot's cast is allocation-free: the per-spell facts are
/// computed once per spell and cached by id, the counters are value-keyed dictionaries; a signal is built only when one is reported, and each
/// (kind, spell, detail) is reported at most once per <see cref="ThrottleMs"/>. World thread only (the spell system's callbacks and the cast handler
/// run there); registered only while QA is enabled.
/// </summary>
internal sealed class PlayerbotQaSpellMonitor : ISpellCastObserver, IBotCastRefusalObserver
{
    /// <summary>
    /// An expected refusal is reported once one bot got it this many times for one spell ... A bot leaves a refused spell out for
    /// <see cref="PlayerbotCombatSpells.RefusalBackoffMs"/> (4 s), so a bot that honours its backoff gets at most 15 refusals a minute: only a bot that
    /// retries faster than its own backoff (a loop that does not learn) reaches this. A rule-driven refusal stream (Overpower at a target without combo
    /// points, every 4 s) never does.
    /// </summary>
    internal const int RepeatThreshold = (int)(RepeatWindowMs / PlayerbotCombatSpells.RefusalBackoffMs) + 5;

    /// <summary>... within this window.</summary>
    internal const long RepeatWindowMs = 60_000;

    /// <summary>Hits in a row with no effect before a spell is reported as having none.</summary>
    internal const int NoEffectRepeats = 3;

    /// <summary>The least time between two reports of the same thing (kind, spell, detail).</summary>
    internal const long ThrottleMs = 60_000;

    /// <summary>The least time between two held-target reports of one creature entry.</summary>
    internal const long HeldTargetThrottleMs = 6 * 60 * 60_000L;

    /// <summary>The Reason prefix of the <see cref="QaSignalKind.Anomaly"/> a creature held at its invincibility threshold is reported with (the entry follows).</summary>
    internal const string HeldTargetReason = "held-target-";

    /// <summary>The Goal (subsystem) of that anomaly.</summary>
    internal const string HeldTargetGoal = "bot-spell";

    /// <summary>The counters are cleared when they hold more entries than this (a bound, not a target).</summary>
    internal const int MaxTracked = 8192;

    private const byte ExpectDamage = 1, ExpectHeal = 2, ExpectAura = 4;

    private readonly IPlayerbotQaSink _sink;
    private readonly PlayerbotQaSpellGapAnalyzer _gaps;
    private readonly Func<Unit?, QaBot?> _identify;
    private readonly Func<long> _clockMs;
    private readonly Func<Player, uint, bool>? _knows;
    private readonly Dictionary<uint, SpellFacts> _spells = [];
    private readonly Dictionary<(QaSignalKind Kind, uint Spell, int Detail), long> _reported = [];
    private readonly Dictionary<(ulong Caster, uint Spell), int> _noEffect = [];
    private readonly Dictionary<(ulong Caster, uint Spell, SpellCastResult Result), (int Count, long FirstMs)> _refusals = [];

    /// <param name="sink">Where the signals go.</param>
    /// <param name="system">The world's spell system (its registries and auras).</param>
    /// <param name="identify">Test seam: which units are bots (default <see cref="PlayerbotQaBots.Identify"/>).</param>
    /// <param name="clockMs">Test seam: a millisecond clock (default <see cref="Environment.TickCount64"/>).</param>
    /// <param name="knows">Whether a bot's spellbook holds a spell (shown for a NotKnown refusal); null when unknown.</param>
    internal PlayerbotQaSpellMonitor(IPlayerbotQaSink sink, SpellSystem system, Func<Unit?, QaBot?>? identify = null, Func<long>? clockMs = null,
        Func<Player, uint, bool>? knows = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _gaps = new PlayerbotQaSpellGapAnalyzer(system ?? throw new ArgumentNullException(nameof(system)));
        _identify = identify ?? PlayerbotQaBots.Identify;
        _clockMs = clockMs ?? (static () => Environment.TickCount64);
        _knows = knows;
    }

    /// <summary>Spells whose facts are cached (a test reads it).</summary>
    internal int CachedSpells => _spells.Count;

    // --- SpellUnsupported -----------------------------------------------------------------------------------------------------------------

    public void OnCast(SpellCast cast)
    {
        if (cast.IsTriggered || _identify(cast.Caster) is not { } bot) return;
        SpellFacts facts = FactsOf(cast.Spell);
        if (facts.Gaps.Length == 0) return;
        long now = _clockMs();
        foreach (QaSpellGap gap in facts.Gaps)
        {
            // A missing handler is reported only when a cast meets it (OnUnsupported); here only the gaps no failing path shows.
            if (!QaSpellGaps.IsSilent(gap.Code)) continue;
            if (!Due(QaSignalKind.SpellUnsupported, cast.Spell.Id, gap.EffectIndex, now)) continue;
            Report(bot, cast.Spell, QaSignalKind.SpellUnsupported, gap.Code, gap.Detail, gap.EffectIndex, gap.EffectName, gap.EffectValue, gap.Aura,
                string.Create(CultureInfo.InvariantCulture, $"a bot cast spell {cast.Spell.Id}: effect {gap.EffectIndex} {QaSpellGaps.Describe(gap.Code, gap.Detail)}"));
        }
    }

    public void OnUnsupported(SpellCast cast, int effectIndex, string kind, uint value)
    {
        if (cast.IsTriggered || _identify(cast.Caster) is not { } bot) return;
        SpellEffectInfo? effect = effectIndex >= 0 && effectIndex < cast.Spell.Effects.Count ? cast.Spell.Effects[effectIndex] : null;
        string effectName = effect?.Effect.ToString() ?? string.Empty;
        int effectValue = effect is null ? 0 : effect.BasePoints + 1;
        long now = _clockMs();
        switch (kind)
        {
            case "effect":
                if (!Due(QaSignalKind.SpellUnsupported, cast.Spell.Id, effectIndex, now)) return;
                Report(bot, cast.Spell, QaSignalKind.SpellUnsupported, QaSpellGaps.Effect, effectName, effectIndex, effectName, effectValue, 0,
                    string.Create(CultureInfo.InvariantCulture, $"a bot cast spell {cast.Spell.Id}: effect {effectIndex} {effectName} has no handler"));
                return;
            case "aura":
                // A Referenced type has no handler but a rule reads it (it works): only an Unsupported type is a gap.
                if (PlayerbotQaSpellGapAnalyzer.LevelOf((AuraType)value) != AuraSupportLevel.Unsupported
                    || !Due(QaSignalKind.SpellUnsupported, cast.Spell.Id, effectIndex, now)) return;
                string aura = ((AuraType)value).ToString();
                Report(bot, cast.Spell, QaSignalKind.SpellUnsupported, QaSpellGaps.Aura, aura, effectIndex, effectName, effectValue, value,
                    string.Create(CultureInfo.InvariantCulture, $"a bot cast spell {cast.Spell.Id}: effect {effectIndex} applies aura {aura}, which nothing implements"));
                return;
            case "implicit target":
                break;
            default:
                return;
        }

        if (!Due(QaSignalKind.SpellUnsupported, cast.Spell.Id, 100 + effectIndex, now)) return;
        string target = string.Create(CultureInfo.InvariantCulture, $"{value} ({(SpellImplicitTarget)value})");
        Report(bot, cast.Spell, QaSignalKind.SpellUnsupported, QaSpellGaps.Target, target, effectIndex, effectName,
            effectValue, 0, string.Create(CultureInfo.InvariantCulture, $"a bot cast spell {cast.Spell.Id}: implicit target {target} is not implemented"));
    }

    // --- SpellNoEffect --------------------------------------------------------------------------------------------------------------------

    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (outcome.Miss != SpellMissInfo.None || _identify(cast.Caster) is not { } bot) return; // a miss, resist, immunity or reflect did not land
        SpellFacts facts = FactsOf(cast.Spell);
        if (facts.Expect == 0) return;
        int selected = outcome.EffectMask & facts.MeasurableMask;
        if (outcome.EffectMask != 0 && selected == 0) return; // nothing measurable aimed at this target (a self part of a mixed spell)
        var key = (cast.Caster.Guid.Value, cast.Spell.Id);
        if (outcome.EffectMask != 0 && (Worked(cast, outcome, facts, selected) || Unmeasurable(outcome, facts, selected) || HeldByDesign(bot, cast, outcome, facts, selected)
            || ZeroByScript(cast, facts, selected) || FullyDiminished(cast, outcome, facts, selected) || outcome.SlotLimited))
        {
            if (_noEffect.ContainsKey(key)) _noEffect[key] = 0;
            return;
        }

        if (_noEffect.Count >= MaxTracked) _noEffect.Clear();
        int misses = _noEffect.GetValueOrDefault(key) + 1;
        if (misses < NoEffectRepeats)
        {
            _noEffect[key] = misses;
            return;
        }

        _noEffect[key] = 0;
        long now = _clockMs();
        if (!Due(QaSignalKind.SpellNoEffect, cast.Spell.Id, 0, now)) return;
        Report(bot, cast.Spell, QaSignalKind.SpellNoEffect, string.Empty, ExpectText(facts.Expect), -1, string.Empty, 0, 0,
            string.Create(CultureInfo.InvariantCulture, $"{NoEffectRepeats} hits in a row of spell {cast.Spell.Id} by one bot dealt no damage, healing or aura{TargetIdentity(outcome.Target)}"));
    }

    /// <summary>
    /// An aura hit dropped by diminishing returns (vmangos Spell::DoSpellHitOnUnit: a fully diminished holder is not applied and no miss is sent) lands with
    /// no aura by design: a warlock's fourth Fear on the same player within the reset window is the rule working, not a spell that does nothing.
    /// </summary>
    private bool FullyDiminished(SpellCast cast, SpellTargetOutcome outcome, SpellFacts facts, int selected)
        => (selected & facts.AuraMask) != 0 && _gaps.System.IsFullyDiminished is { } diminished && diminished(cast.Caster, outcome.Target, cast.Spell);

    /// <summary>
    /// A pure damage hit on a unit held at its invincibility threshold takes no health by design (vmangos Unit::DealDamage, Unit.cpp:825-850: nothing at or
    /// below the threshold is taken): a creature whose EventAI sets the threshold (the Blasted Lands quest creatures 7668-7671 stay at 1 health) or a player in
    /// Spirit of Redemption. A player held with no such aura is a death prevention that leaked, and a hit with a heal or aura part (which can fail on its own)
    /// or on a target above its threshold stays reported as SpellNoEffect. A held creature is excused from SpellNoEffect but not hidden: a script that leaks a
    /// threshold (an unkillable boss) looks the same, so each creature entry is reported once per <see cref="HeldTargetThrottleMs"/> as an
    /// <see cref="QaSignalKind.Anomaly"/> (<see cref="HeldTargetGoal"/>, <see cref="HeldTargetReason"/> + entry) naming the entry and the threshold. It appears
    /// in the QA report only and never files an issue (PlayerbotQaPublisher.Eligible skips it).
    /// </summary>
    private bool HeldByDesign(QaBot bot, SpellCast cast, SpellTargetOutcome outcome, SpellFacts facts, int selected)
    {
        Unit target = outcome.Target;
        uint threshold = target.InvincibilityHpThreshold;
        if ((selected & ~facts.DamageMask) != 0 || threshold == 0 || target.Health > threshold) return false;
        if (target is Creature creature)
        {
            ReportHeldCreature(bot, cast, creature, threshold);
            return true;
        }

        return target is Player && _gaps.System.HasAura(target, SpellSystem.SpiritOfRedemptionSpell);
    }

    private void ReportHeldCreature(QaBot bot, SpellCast cast, Creature creature, uint threshold)
    {
        long now = _clockMs();
        if (!Due(QaSignalKind.Anomaly, 0, (int)creature.Entry, now, HeldTargetThrottleMs)) return;
        string message = string.Create(CultureInfo.InvariantCulture,
            $"creature {creature.Entry} is held at its invincibility threshold {threshold} (health {creature.Health}/{creature.MaxHealth}): spell {cast.Spell.Id} deals nothing to it by design, but a script that leaks the threshold looks the same");
        _sink.Report(new QaSignal
        {
            Kind = QaSignalKind.Anomaly, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Goal = HeldTargetGoal,
            Reason = HeldTargetReason + creature.Entry.ToString(CultureInfo.InvariantCulture), Target = creature.Entry,
            Detail = string.Create(CultureInfo.InvariantCulture, $"entry {creature.Entry}, threshold {threshold}"), Message = message,
        });
    }

    /// <summary>
    /// A script's cast (CastCustomSpell) whose selected damage or heal effects all carry an explicit value of 0 or less does nothing by design: Frenzied
    /// Regeneration's tick casts heal 22845 with <c>rage * amount / 10</c> base points (vmangos SpellAuras.cpp:1417-1433), 0 for a druid with no rage.
    /// </summary>
    private bool ZeroByScript(SpellCast cast, SpellFacts facts, int selected)
        => cast.IsTriggered && (selected & facts.AuraMask) == 0 && _gaps.System.CustomValueIsNonPositive(cast.Caster, cast.Spell, selected);

    private bool Worked(SpellCast cast, SpellTargetOutcome outcome, SpellFacts facts, int selected)
    {
        if (outcome.Damage > 0 || outcome.Healing > 0) return true;
        Unit target = outcome.Target;
        if ((selected & facts.HealMask) != 0 && target.Health >= target.MaxHealth) return true; // an overheal of a full target works
        if ((selected & facts.DamageMask) != 0 && !target.IsAlive) return true; // already dead or killed by another part
        if ((selected & facts.AuraMask) != 0)
        {
            IReadOnlyList<SpellAuraHolder> holders = _gaps.System.GetAuras(target);
            for (int i = 0; i < holders.Count; i++)
            {
                SpellAuraHolder holder = holders[i];
                if (holder.Spell.Id == cast.Spell.Id && holder.CasterGuid == cast.Caster.Guid) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A damage hit on a target with an absorb shield (SCHOOL_ABSORB, MANA_SHIELD) cannot be judged: the outcome counts damage dealt after absorbs, so a fully
    /// absorbed hit looks like none. An INSTAKILL deals its damage with no absorb step (vmangos Spell::EffectInstaKill, SpellEffects.cpp:283 is a plain
    /// DealDamage of the whole health), so only the other damage effects qualify. Only reached on the rare path where nothing measurable happened.
    /// </summary>
    private bool Unmeasurable(SpellTargetOutcome outcome, SpellFacts facts, int selected)
    {
        if ((selected & facts.DamageMask & ~facts.InstakillMask) == 0) return false;
        if (outcome.Absorbed > 0 || outcome.Resisted > 0) return true;
        IReadOnlyList<SpellAuraHolder> holders = _gaps.System.GetAuras(outcome.Target);
        for (int i = 0; i < holders.Count; i++)
        {
            if (holders[i].HasAura(AuraType.SchoolAbsorb) || holders[i].HasAura(AuraType.ManaShield)) return true;
        }

        return false;
    }

    /// <summary>
    /// Who took the hits that did nothing, for the evidence line: kind and entry, health, the invincibility threshold (a leaked Spirit of Redemption or
    /// a death-prevention that never ended shows here), whether an absorb aura is present and whether a quest settlement holds the unit.
    /// </summary>
    private string TargetIdentity(Unit target)
    {
        bool absorb = false;
        IReadOnlyList<SpellAuraHolder> holders = _gaps.System.GetAuras(target);
        for (int i = 0; i < holders.Count && !absorb; i++)
        {
            absorb = holders[i].HasAura(AuraType.SchoolAbsorb) || holders[i].HasAura(AuraType.ManaShield);
        }

        string kind = target switch
        {
            Player => "player",
            Creature creature => string.Create(CultureInfo.InvariantCulture, $"creature {creature.Entry}"),
            _ => target.GetType().Name,
        };
        bool settling = target is Player { IsQuestSettlementPending: true };
        return string.Create(CultureInfo.InvariantCulture,
            $"; last target: {kind}, health {target.Health}/{target.MaxHealth}, invincibility threshold {target.InvincibilityHpThreshold}, absorb aura {(absorb ? "yes" : "no")}, quest settlement pending {(settling ? "yes" : "no")}");
    }

    private static string ExpectText(byte expect)
    {
        var parts = new List<string>(3);
        if ((expect & ExpectDamage) != 0) parts.Add("damage");
        if ((expect & ExpectHeal) != 0) parts.Add("healing");
        if ((expect & ExpectAura) != 0) parts.Add("an aura");
        return "expected " + string.Join(" or ", parts);
    }

    // --- SpellRefused ---------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The server refused <paramref name="player"/>'s cast of <paramref name="spellId"/> with <paramref name="result"/> (SpellHandlers, CMSG_CAST_SPELL of a
    /// managed session). Nothing for a real player or <see cref="SpellCastResult.CastOk"/>.
    /// </summary>
    public void OnCastRefused(Player player, uint spellId, SpellCastResult result)
    {
        if (result == SpellCastResult.CastOk || _identify(player) is not { } bot) return;
        long now = _clockMs();
        QaRefusalRule rule = PlayerbotQaSpellRefusals.Of(result);
        long first = now;
        int count = 0;
        if (rule.Class == QaRefusalClass.Expected)
        {
            if (_refusals.Count >= MaxTracked) _refusals.Clear();
            var key = (player.Guid.Value, spellId, result);
            (count, first) = _refusals.GetValueOrDefault(key);
            if (count == 0 || now - first > RepeatWindowMs)
            {
                count = 0;
                first = now;
            }

            count++;
            if (count < RepeatThreshold)
            {
                _refusals[key] = (count, first);
                return;
            }

            _refusals[key] = (0, now);
        }

        if (!Due(QaSignalKind.SpellRefused, spellId, (int)result, now)) return;
        string detail = rule.Class == QaRefusalClass.Expected
            ? string.Create(CultureInfo.InvariantCulture, $"{count} times in {Math.Max(1, (now - first) / 1000)} s")
            : result == SpellCastResult.NotKnown && _knows is not null
                ? (_knows(player, spellId) ? "the spell is in the bot's spellbook" : "the spell is not in the bot's spellbook")
                : string.Empty;
        SpellInfo? spell = _gaps.System.Store.Get(spellId);
        string message = string.Create(CultureInfo.InvariantCulture, $"the server refused a bot's cast of spell {spellId} with {result}")
            + (detail.Length != 0 ? " (" + detail + ")" : string.Empty);
        if (spell is null)
        {
            _sink.Report(new QaSignal
            {
                Kind = QaSignalKind.SpellRefused, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Spell = spellId, Reason = result.ToString(),
                Detail = detail, Message = message,
            });
            return;
        }

        Report(bot, spell, QaSignalKind.SpellRefused, result.ToString(), detail, -1, string.Empty, 0, 0, message);
    }

    // --- shared -----------------------------------------------------------------------------------------------------------------------------

    private void Report(QaBot bot, SpellInfo spell, QaSignalKind kind, string reason, string detail, int effectIndex, string effectName, int effectValue,
        uint aura, string message)
        => _sink.Report(new QaSignal
        {
            Kind = kind, AtUtc = DateTime.UtcNow, BotKey = bot.Key, BotClass = bot.Class, Spell = spell.Id, SpellName = spell.Name, SpellRank = spell.Rank,
            EffectIndex = effectIndex, EffectName = effectName, EffectValue = effectValue, Aura = aura, Reason = reason, Detail = detail, Message = message,
        });

    /// <summary>Whether (kind, spell, detail) was not reported within <see cref="ThrottleMs"/>; if so, it is marked as reported now.</summary>
    private bool Due(QaSignalKind kind, uint spell, int detail, long now, long windowMs = ThrottleMs)
    {
        var key = (kind, spell, detail);
        if (_reported.TryGetValue(key, out long last) && now - last < windowMs) return false;
        if (_reported.Count >= MaxTracked) _reported.Clear();
        _reported[key] = now;
        return true;
    }

    /// <summary>The cached facts of a spell (computed on its first cast; recomputed when the spell table hands out a new instance).</summary>
    private SpellFacts FactsOf(SpellInfo spell)
    {
        if (_spells.TryGetValue(spell.Id, out SpellFacts? facts) && ReferenceEquals(facts.Spell, spell)) return facts;
        if (_spells.Count >= MaxTracked) _spells.Clear();
        facts = Compute(spell);
        _spells[spell.Id] = facts;
        return facts;
    }

    private SpellFacts Compute(SpellInfo spell)
    {
        int damage = 0, heal = 0, aura = 0, kill = 0;
        if (!spell.IsPassive)
        {
            IReadOnlyList<SpellEffectInfo> effects = spell.Effects;
            for (int i = 0; i < effects.Count; i++)
            {
                SpellEffectInfo effect = effects[i];
                switch (effect.Effect)
                {
                    case SpellEffectName.SchoolDamage when effect.BasePoints + 1 > 0:
                    case SpellEffectName.HealthLeech when effect.BasePoints + 1 > 0:
                    case SpellEffectName.WeaponDamage:
                    case SpellEffectName.WeaponDamageNoschool:
                    case SpellEffectName.NormalizedWeaponDmg:
                    case SpellEffectName.WeaponPercentDamage:
                        damage |= 1 << i;
                        break;
                    case SpellEffectName.Instakill: // a kill is the effect (Suicide, 27965: the kill also strips the aura the spell applies)
                        damage |= 1 << i;
                        kill |= 1 << i;
                        break;
                    case SpellEffectName.Heal when effect.BasePoints + 1 > 0:
                        heal |= 1 << i;
                        break;
                    case SpellEffectName.ApplyAura when effect.AuraType != AuraType.None:
                        aura |= 1 << i;
                        break;
                }
            }
        }

        byte expect = (byte)((damage != 0 ? ExpectDamage : 0) | (heal != 0 ? ExpectHeal : 0) | (aura != 0 ? ExpectAura : 0));
        return new SpellFacts(spell, _gaps.Of(spell), damage, heal, aura, expect, kill);
    }

    private sealed record SpellFacts(SpellInfo Spell, QaSpellGap[] Gaps, int DamageMask, int HealMask, int AuraMask, byte Expect, int InstakillMask)
    {
        public int MeasurableMask { get; } = DamageMask | HealMask | AuraMask;
    }
}

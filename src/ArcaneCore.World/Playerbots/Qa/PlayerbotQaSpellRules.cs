using System.Collections.Frozen;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>The gap codes of <see cref="QaSignalKind.SpellUnsupported"/> (<see cref="QaSignal.Reason"/>) and their words.</summary>
internal static class QaSpellGaps
{
    /// <summary>The effect has no handler (<see cref="SpellSystem.HasEffectHandler"/>): it is skipped.</summary>
    internal const string Effect = "effect";

    /// <summary>The aura type has no handler and nothing else reads it (<see cref="AuraSupportLevel.Unsupported"/>): the aura only takes a slot.</summary>
    internal const string Aura = "aura";

    /// <summary>A DUMMY or OVERRIDE_CLASS_SCRIPTS aura nothing reads (no script, proc script, dummy-aura case or known reader).</summary>
    internal const string NoConsumer = "no-consumer";

    /// <summary>A DUMMY effect no spell script claims, on a spell with no other working effect: the spell does nothing.</summary>
    internal const string Dummy = "dummy";

    /// <summary>A SCRIPT_EFFECT with no script-effect or spell script, on a spell with no other working effect: the spell does nothing.</summary>
    internal const string Script = "script";

    /// <summary>An implicit target type the spell system cannot select (seen only while a bot casts it).</summary>
    internal const string Target = "target";

    /// <summary>
    /// Whether a gap is advisory: listed and counted, never published. A DUMMY or OVERRIDE_CLASS_SCRIPTS aura can be read by any rule that looks for the
    /// spell id, the icon or the family (the registries cannot know them all), so "read by nothing" is a lead, not a defect.
    /// </summary>
    internal static bool IsAdvisory(string code) => string.Equals(code, NoConsumer, StringComparison.Ordinal);

    /// <summary>The gap in a few words for a title: <c>aura ModStun not implemented</c>.</summary>
    internal static string Describe(string code, string detail) => code switch
    {
        Effect => $"{detail} not implemented",
        Aura => $"aura {detail} not implemented",
        NoConsumer => $"aura {detail} read by nothing",
        Dummy => "DUMMY with no script",
        Script => "SCRIPT_EFFECT with no script",
        Target => $"implicit target {detail} not implemented",
        _ => $"{code} {detail}".Trim(),
    };

    /// <summary>The cause text of a gap.</summary>
    internal static string Explain(string code, string detail) => code switch
    {
        Effect => $"the spell effect {detail} has no handler in the spell system, so the effect is skipped when the spell lands (vmangos SpellEffects.cpp has it)",
        Aura => $"the aura type {detail} has no handler and nothing reads it (AuraSupport: Unsupported), so the aura only occupies a slot and changes nothing",
        NoConsumer => $"the {detail} aura does nothing by itself (vmangos HandleAuraDummy / HandleNoImmediateEffect) and no script, proc script, dummy-aura case or known rule reads it for this spell",
        Dummy => "the DUMMY effect is the spell's only behaviour and no spell script claims the spell: vmangos implements it in Spell::EffectDummy, here the cast does nothing",
        Script => "the SCRIPT_EFFECT is the spell's only behaviour and neither the script-effect table nor a spell script claims the spell: here the cast does nothing",
        Target => $"the implicit target {detail} is not implemented, so the effect selects no unit when the spell lands",
        _ => $"{code} {detail}".Trim(),
    };
}

/// <summary>How bot QA treats one refused cast result.</summary>
internal enum QaRefusalClass
{
    /// <summary>A normal refusal of play (no mana, cooldown, range, line of sight, a moving caster): reported only when one bot keeps repeating it.</summary>
    Expected,

    /// <summary>A refusal a bot should never get from its own rotation (a server error, an unknown spell, a target the spell cannot take): reported at once.</summary>
    Suspicious,
}

/// <summary>One row of the refusal table.</summary>
internal readonly record struct QaRefusalRule(QaRefusalClass Class, QaCause Cause, string Why);

/// <summary>
/// The refusal table of <see cref="QaSignalKind.SpellRefused"/>: every <see cref="SpellCastResult"/> a bot's cast can get, as Expected (bot logic: only a
/// bot that repeats the same refusal <see cref="PlayerbotQaSpellMonitor.RepeatThreshold"/> times inside <see cref="PlayerbotQaSpellMonitor.RepeatWindowMs"/>
/// is reported, as a bot that does not learn) or Suspicious (reported at once). A result not listed as suspicious is expected. Pure.
/// </summary>
internal static class PlayerbotQaSpellRefusals
{
    /// <summary>The suspicious results and why each one is.</summary>
    internal static FrozenDictionary<SpellCastResult, QaRefusalRule> Suspicious { get; } = new Dictionary<SpellCastResult, QaRefusalRule>
    {
        [SpellCastResult.Error] = new(QaRefusalClass.Suspicious, QaCause.NotImplemented, "the server answered SPELL_FAILED_ERROR: an internal failure, not a rule of play"),
        [SpellCastResult.Unknown] = new(QaRefusalClass.Suspicious, QaCause.NotImplemented, "the server answered SPELL_FAILED_UNKNOWN: no rule names the refusal"),
        [SpellCastResult.NotFound] = new(QaRefusalClass.Suspicious, QaCause.MissingData, "the spell is not in the spell table, yet a bot cast it"),
        [SpellCastResult.SpellUnavailable] = new(QaRefusalClass.Suspicious, QaCause.NotImplemented, "the server says the spell is unavailable for a spell the bot chose from its book"),
        [SpellCastResult.NotKnown] = new(QaRefusalClass.Suspicious, QaCause.BehaviourBug,
            "the server says the bot does not know the spell: the rotation picked a spell its spellbook does not hold, or the book and the server disagree"),
        [SpellCastResult.BadTargets] = new(QaRefusalClass.Suspicious, QaCause.BehaviourBug,
            "the target the bot chose is not a valid target of the spell: the rotation's target rule or the server's target check is wrong"),
        [SpellCastResult.BadImplicitTargets] = new(QaRefusalClass.Suspicious, QaCause.BehaviourBug,
            "the spell found no implicit target: the rotation cast it where it cannot land, or the target selection is wrong"),
        [SpellCastResult.LevelRequirement] = new(QaRefusalClass.Suspicious, QaCause.BehaviourBug, "the bot cast a spell above its level"),
        [SpellCastResult.TrainingPoints] = new(QaRefusalClass.Suspicious, QaCause.BehaviourBug, "the bot tried to learn a pet spell without training points"),
    }.ToFrozenDictionary();

    /// <summary>The rule of <paramref name="result"/> (suspicious when listed, otherwise expected).</summary>
    internal static QaRefusalRule Of(SpellCastResult result)
        => Suspicious.TryGetValue(result, out QaRefusalRule rule)
            ? rule
            : new QaRefusalRule(QaRefusalClass.Expected, QaCause.BehaviourBug,
                "an ordinary refusal of play, but one bot kept trying the same cast and getting it: the bot does not learn from the refusal");

    /// <summary>The rule of a refusal's <see cref="QaSignal.Reason"/> (a <see cref="SpellCastResult"/> name), null when it names none.</summary>
    internal static QaRefusalRule? Of(string reason)
        => Enum.TryParse(reason, ignoreCase: false, out SpellCastResult result) && Enum.IsDefined(result) ? Of(result) : null;
}

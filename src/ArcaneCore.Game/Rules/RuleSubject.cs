namespace ArcaneCore.Game.Rules;

/// <summary>
/// What subject filters test about a subject (<c>rule_subject_filter</c>). A host fills it from its own object; a value the host does not
/// know is 0, which only an "any" filter column matches.
/// </summary>
/// <param name="Class">Class id (1-based); 0 = none.</param>
/// <param name="Race">Race id (1-based); 0 = none.</param>
/// <param name="Level">Level; 0 = none.</param>
/// <param name="Team">1 Alliance, 2 Horde, 0 none.</param>
public readonly record struct RuleSubjectFacts(
    byte Class, byte Race, byte Level, byte Team, uint MapId, uint ZoneId, uint AreaId, RuleSubjectFlags Flags);

/// <summary>
/// One subject of a host (a bot, a creature, a game object, ...) as the rule engine sees it. Implementations are the host's existing
/// objects or thin adapters held by them; the evaluator only reads these members, so a call allocates nothing.
/// </summary>
public interface IRuleSubject
{
    /// <summary>The full object guid: the stable-share hash input and the <c>.rules trace</c> key.</summary>
    ulong Guid { get; }

    bool IsInCombat { get; }

    RuleSubjectFacts Facts { get; }
}

/// <summary>A subject that can say a <c>rule_text</c> (the <see cref="RuleCapabilities.Speak"/> capability).</summary>
public interface IRuleSpeaker : IRuleSubject
{
    void Say(RuleText text, IRuleSubject? target);
}

/// <summary>A subject that can cast a spell (the <see cref="RuleCapabilities.Cast"/> capability).</summary>
public interface IRuleCaster : IRuleSubject
{
    void Cast(int spellId, int flags, IRuleSubject? target);
}

/// <summary>A loaded <c>rule_text</c> row.</summary>
public sealed record RuleText(int Id, string Content, int Sound, byte Type, byte Language, int Emote, int BroadcastTextId);

/// <summary>
/// The condition check a host hands the evaluator: whether <c>conditions</c> row (the <c>conditionId</c> argument) holds for the subject
/// (with the invoker, when the event has one). Null means undecidable; the evaluator treats it as not satisfied (fail closed, design 5.5).
/// </summary>
public interface IRuleConditionGate
{
    bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker);
}

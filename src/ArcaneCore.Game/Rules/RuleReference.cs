using System.Globalization;
using System.Text;

namespace ArcaneCore.Game.Rules;

/// <summary>
/// The self-description of a <see cref="RuleVocabulary"/>: the lines <c>.rules vocab</c> prints and the generated
/// <c>docs/reference/rules.md</c> (checked by <c>RuleReferenceTests</c>; regenerate with the docs update switch).
/// </summary>
public static class RuleReference
{
    /// <summary>One line per primitive the host (or, for null, every host) can use: kind, id, name, parameters and description.</summary>
    public static IReadOnlyList<string> VocabularyLines(RuleVocabulary vocabulary, RuleHostKind? host = null)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        IReadOnlyList<RulePrimitiveInfo> primitives = host is { } h ? vocabulary.For(h) : vocabulary.Primitives;
        var lines = new List<string>();
        foreach (RulePrimitiveInfo p in primitives)
        {
            string scope = p.Host == RuleHostKind.Any ? "shared" : HostLabel(vocabulary, p.Host);
            string module = p.IsBuiltIn ? "" : " [module]";
            lines.Add($"{Kind(p.Kind)} {p.Id} {p.Name}({string.Join(", ", p.Parameters.Select(Param))}) [{scope}]{module}: {p.Description}");
        }

        return lines;
    }

    /// <summary>docs/reference/rules.md for <paramref name="vocabulary"/>.</summary>
    public static string RenderMarkdown(RuleVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        var b = new StringBuilder();
        b.Append("# Rule engine vocabulary\n\n");
        b.Append("Generated from the rule primitives and hosts of the Game assembly (`RuleVocabulary.Default`). Regenerate with ")
            .Append("`ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`. The design is ")
            .Append("[the rule engine design](../superpowers/specs/2026-10-28-rule-engine-design.md); `.rules vocab` prints the same list in game.\n\n");

        b.Append("## Hosts\n\n");
        IReadOnlyList<RuleHostDefinition> hosts = vocabulary.Hosts;
        if (hosts.Count == 0)
        {
            b.Append("No host is registered in this build, so every `rule` row is rejected with \"host N is not registered\". ")
                .Append("The host ids are reserved as follows.\n\n");
        }

        b.Append("| `host` | Name | Registered | Owner kinds | Capabilities |\n| ---: | --- | --- | --- | --- |\n");
        foreach (RuleHostKind kind in Enum.GetValues<RuleHostKind>().Where(k => k != RuleHostKind.Any))
        {
            RuleHostDefinition? definition = vocabulary.FindHost(kind);
            string owners = definition is null ? "-" : string.Join(", ", definition.Owners.Select(o => $"{(byte)o} {o}"));
            string caps = definition is null ? "-" : definition.Capabilities.ToString();
            b.Append($"| {(byte)kind} | {definition?.Name ?? RuleHostNames.Of(kind)} | {(definition is null ? "no" : "yes")} | {owners} | {caps} |\n");
        }

        b.Append("\n## Owner kinds (`owner_kind`)\n\n| Id | Kind |\n| ---: | --- |\n");
        foreach (RuleOwnerKind kind in Enum.GetValues<RuleOwnerKind>())
        {
            b.Append($"| {(byte)kind} | {kind}{(kind == RuleOwnerKind.Global ? " (owner_id 0: every subject of the host)" : "")} |\n");
        }

        b.Append("\n## Rule flags (`rule.flags`)\n\n| Bit | Flag |\n| ---: | --- |\n");
        foreach (RuleFlags flag in Enum.GetValues<RuleFlags>().Where(f => f != RuleFlags.None))
        {
            b.Append($"| 0x{(int)flag:X2} | {flag} |\n");
        }

        b.Append("\n## Subject flags (`rule_subject_filter.subject_flags`)\n\n| Bit | Flag |\n| ---: | --- |\n");
        foreach (RuleSubjectFlags flag in Enum.GetValues<RuleSubjectFlags>().Where(f => f != RuleSubjectFlags.None))
        {
            b.Append($"| 0x{(int)flag:X2} | {flag} |\n");
        }

        b.Append("\n## Evaluation\n\n")
            .Append("For each rule of the fired event, lowest `priority` first (ties by `id`), the evaluator checks in this order: the event's ")
            .Append("parameters, `phase_mask` (bit n = phase n, 0 = every phase), the combat flags, the once-only rule (without Repeatable a rule ")
            .Append("fires once until the host resets the subject), the cooldown, the subject filter's columns, the filter's `share_pct`, then ")
            .Append("`chance_pct` and last `condition_id` (an undecidable condition fails closed). Only a rule that passed every check draws a ")
            .Append("cooldown in `cooldown_min_ms..cooldown_max_ms`.\n\n")
            .Append("`phase_mask` is a positive mask: bit n set means the rule fires in phase n (0..31, the phase `set_phase` sets), and 0 ")
            .Append("means every phase. It is neither EventAI's mask (cmangos `event_inverse_phase_mask`, where a set bit n means the event does ")
            .Append("**not** fire in phase n) nor SmartAI's (TrinityCore `event_phase_mask`, where bit n means phase n + 1 and phase 0 matches no ")
            .Append("non-zero mask); the EventAI and SmartAI converters translate them. `cooldown_max_ms` and an action's `delay_ms` are at most ")
            .Append(RuleCatalog.MaxCooldownMs.ToString(CultureInfo.InvariantCulture)).Append(" ms (24 hours); a larger value is a rejected row.\n\n")
            .Append("`chance_pct` is a fresh roll per firing. With the StableShare flag it is instead a stable share of subjects, as `share_pct` ")
            .Append("is: a subject is selected when `bucket(seed, rule_id, guid) < pct`, where bucket is two SplitMix64 finalisations of the ")
            .Append("three inputs, mod 100 (`RuleShare.Bucket`). A filter uses its `share_seed`; the StableShare chance uses the filter's seed ")
            .Append($"(0 without a filter) xor 0x{RuleShare.StableChanceSalt:X8}, so the two shares are independent. The same subjects stay selected ")
            .Append("across ticks and restarts; a new seed reshuffles them.\n\n")
            .Append("Every action names a target. An action's `delay_ms` counts from the previous action of its sequence; the rest of the ")
            .Append($"sequence then waits in one of the subject's {RuleSubjectState.PendingCapacity} pending slots. Lists started one inside another with no delay in between nest at most ")
            .Append($"{RuleEvaluator.MaxListDepth} deep: a longer chain, or a list that starts itself again with no delay in between, is rejected at load (with the rule that starts it); a delay anywhere in the chain breaks it, and a start that still gets too deep at run time fails and is counted.\n\n")
            .Append($"Ids below {RuleCatalog.CustomIdStart} are shipped or converted content; server-authored rows use {RuleCatalog.CustomIdStart} and up. ")
            .Append($"`playerbot_goal.goal_id` is a `PlayerbotGoalKind` value, a ladder rung ({RuleCatalog.RungGoalIdStart} to {RuleCatalog.RungGoalIdEnd}: 100 plus the rung number), or {RuleCatalog.CustomGoalIdStart} and up for a custom goal. A goal a rule proposes needs a non-zero `driver`.\n\n");

        b.Append("## Parameter kinds\n\n| Kind | Checked at load |\n| --- | --- |\n");
        foreach (RuleParamKind kind in Enum.GetValues<RuleParamKind>())
        {
            b.Append($"| {kind} | {ParamKindRule(kind)} |\n");
        }

        foreach (RulePrimitiveKind kind in Enum.GetValues<RulePrimitiveKind>())
        {
            b.Append($"\n## {Kind(kind)}s\n\n");
            RulePrimitiveInfo[] rows = [.. vocabulary.Primitives.Where(p => p.Kind == kind)];
            if (rows.Length == 0)
            {
                b.Append($"None in this build (each host brings its own {Kind(kind)}s).\n");
                continue;
            }

            b.Append("| Id | Name | Host | Parameters | Description |\n| ---: | --- | --- | --- | --- |\n");
            foreach (RulePrimitiveInfo p in rows)
            {
                string parameters = p.Parameters.Count == 0 ? "-" : string.Join("<br>", p.Parameters.Select(x => $"{x.Index} `{x.Name}` ({x.Kind}): {x.Description}"));
                b.Append($"| {p.Id} | `{p.Name}` | {(p.Host == RuleHostKind.Any ? "shared" : HostLabel(vocabulary, p.Host))} | {parameters} | {p.Description} |\n");
            }
        }

        return b.ToString();
    }

    private static string ParamKindRule(RuleParamKind kind) => kind switch
    {
        RuleParamKind.Int => "any value",
        RuleParamKind.Bool => "0 or 1",
        RuleParamKind.Percent => "0..100",
        RuleParamKind.Milliseconds => "0 or more",
        RuleParamKind.Phase => "0..31",
        RuleParamKind.SpellId => "above 0, and a known spell when the host supplies the spell store",
        RuleParamKind.CreatureEntry => "above 0, and a known creature entry when the host supplies the templates",
        RuleParamKind.TextId => "a loaded rule_text id",
        RuleParamKind.ConditionId => "a loaded conditions row",
        RuleParamKind.RuleListId => "a rule_list id that loads for the rule's host",
        RuleParamKind.GoalId => "a loaded playerbot_goal id",
        RuleParamKind.NonNegative => "0 or more",
        _ => "-",
    };

    private static string Param(RuleParamInfo p) => $"{p.Name}:{p.Kind}";

    private static string Kind(RulePrimitiveKind kind) => kind switch
    {
        RulePrimitiveKind.Event => "event",
        RulePrimitiveKind.Action => "action",
        _ => "target",
    };

    private static string HostLabel(RuleVocabulary vocabulary, RuleHostKind host) => vocabulary.FindHost(host)?.Name ?? RuleHostNames.Of(host);
}

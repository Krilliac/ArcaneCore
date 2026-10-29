using System.Text.RegularExpressions;
using ArcaneCore.Data.World.Rules;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Tests.Hygiene;
using ArcaneCore.Kernel.Rules.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules.Creatures;

/// <summary>
/// The creature host's vocabulary (design 3, inventory 12): the host exists, every EventAI event, action and target the executor supports
/// has a unified creature primitive, and <c>docs/reference/rules-conversion.md</c> lists each of them.
/// </summary>
public sealed class CreatureVocabularyTests
{
    private static readonly RuleVocabulary Vocabulary = RuleVocabulary.Default;

    [Fact]
    public void TheCreatureHost_IsRegistered_WithTheConverterHostId()
    {
        RuleHostDefinition? host = Vocabulary.FindHost(RuleHostKind.Creature);

        Assert.NotNull(host);
        Assert.Equal("creature", host!.Name);
        Assert.Equal(CreatureRuleIds.Host, (byte)RuleHostKind.Creature);
        Assert.Contains(RuleOwnerKind.Entry, host.Owners);
        Assert.Contains(RuleOwnerKind.Guid, host.Owners);
        Assert.True(host.Capabilities.HasFlag(RuleCapabilities.Speak));
        Assert.True(host.Capabilities.HasFlag(RuleCapabilities.Cast));
    }

    [Fact]
    public void EveryEventAiEventType_HasACreatureEvent()
    {
        byte[] supported = [.. EventAiRegistry.Default.EventTypes.Where(EventAiRegistry.Default.IsBuiltInEvent).Order()];

        Assert.Equal(34, supported.Length);
        Assert.Equal(supported, EventAiRuleConverter.Events.Keys.Order());
        foreach (byte type in supported)
        {
            short id = EventAiRuleConverter.Events[type].RuleId;
            Assert.NotNull(Vocabulary.FindEvent(RuleHostKind.Creature, id));
        }

        Assert.Equal(EventAiRuleConverter.Events.Count, EventAiRuleConverter.Events.Values.Select(m => m.RuleId).Distinct().Count());
    }

    [Fact]
    public void EveryEventAiActionType_HasACreatureAction()
    {
        byte[] supported = [.. EventAiRegistry.Default.ActionTypes.Where(EventAiRegistry.Default.IsBuiltInAction).Order()];

        Assert.Equal(supported, EventAiRuleConverter.Actions.Keys.Order());
        foreach (byte type in supported)
        {
            short id = EventAiRuleConverter.Actions[type].RuleId;
            Assert.NotNull(Vocabulary.For(RuleHostKind.Creature).FirstOrDefault(p => p.Kind == RulePrimitiveKind.Action && p.Id == id));
        }
    }

    [Fact]
    public void EveryEventAiTarget_HasACreatureTarget()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15], EventAiRuleConverter.Targets.Keys.Order());
        foreach (short id in EventAiRuleConverter.Targets.Values)
        {
            Assert.NotNull(Vocabulary.FindTarget(RuleHostKind.Creature, id));
        }

        Assert.NotNull(Vocabulary.FindTarget(RuleHostKind.Creature, CreatureRuleIds.TargetTextAudience));
    }

    [Fact]
    public void CreaturePrimitiveIds_StayClearOfTheSharedIds()
    {
        foreach (RulePrimitiveInfo info in Vocabulary.Primitives.Where(p => p.Host == RuleHostKind.Creature))
        {
            int floor = info.Kind switch
            {
                RulePrimitiveKind.Event => 1,
                RulePrimitiveKind.Target => 10,
                _ => 100,
            };
            Assert.True(info.Id >= floor, $"{info.Name} has id {info.Id}");
            Assert.StartsWith("creature.", info.Name, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(info.Description), info.Name);
        }
    }

    [Fact]
    public void TheConversionDoc_ListsEveryMappedType()
    {
        string doc = File.ReadAllText(Path.Combine(RepoHygieneScanner.FindRepoRoot(), "docs", "reference", "rules-conversion.md"));

        foreach ((byte type, EventAiRuleConverter.EventMap map) in EventAiRuleConverter.Events)
        {
            AssertRow(doc, "Events", type, Name(RulePrimitiveKind.Event, map.RuleId));
        }

        foreach ((byte type, EventAiRuleConverter.ActionMap map) in EventAiRuleConverter.Actions)
        {
            AssertRow(doc, "Actions", type, Name(RulePrimitiveKind.Action, map.RuleId));
        }

        foreach ((int type, short id) in EventAiRuleConverter.Targets)
        {
            AssertRow(doc, "Targets", type, Name(RulePrimitiveKind.Target, id));
        }
    }

    private static string Name(RulePrimitiveKind kind, short id)
        => Vocabulary.For(RuleHostKind.Creature).First(p => p.Kind == kind && p.Id == id).Name;

    private static void AssertRow(string doc, string section, int type, string primitive)
    {
        Match header = Regex.Match(doc, $@"^## {section}\b.*?$(?<body>.*?)(?=^## |\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(header.Success, $"section '{section}' is missing from rules-conversion.md");
        bool listed = header.Groups["body"].Value.Split('\n')
            .Any(line => Regex.IsMatch(line, $@"^\|\s*{type}\s*\|") && line.Contains($"`{primitive}`", StringComparison.Ordinal));
        Assert.True(listed, $"{section}: EventAI type {type} -> `{primitive}` has no row in rules-conversion.md");
    }
}

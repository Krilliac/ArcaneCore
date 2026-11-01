using ArcaneCore.Data.World.Rules;
using ArcaneCore.Game.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>
/// Pins the hard-coded rule patterns the inventory (spec section 9) still lists, so new ones cannot creep back into code. Each pin is an exact
/// list: a new site fails (add a rule primitive or table row instead), and a site that no longer matches fails too (shrink the list when a
/// conversion lands, so the pin stays true).
/// </summary>
public sealed class RuleInventoryTests
{
    /// <summary>Every source file that still names a SCRIPT_COMMAND_* (the relay script commands, their quest hooks and their importer).</summary>
    private static readonly string[] ScriptCommandFiles =
    [
        "ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.Refresh.cs",
        "ArcaneCore.Game/Creatures/AI/CreatureAiServices.cs",
        "ArcaneCore.Game/Creatures/AI/IScriptQuestEvents.cs",
        "ArcaneCore.Game/Creatures/Creature.cs",
        "ArcaneCore.Game/Creatures/Creature.ScriptOverrides.cs",
        "ArcaneCore.Game/Creatures/CreatureMapSystem.RelayCommands.cs",
        "ArcaneCore.Game/Creatures/CreatureMapSystem.RelayCommandsExtra.cs",
        "ArcaneCore.Game/Creatures/CreatureMapSystem.RelayFormation.cs",
        "ArcaneCore.Game/Creatures/CreatureMapSystem.RelayScripts.cs",
        "ArcaneCore.Game/Creatures/CreatureMapSystem.RelaySeams.cs",
        "ArcaneCore.Game/Creatures/CreatureQuestScripts.cs",
        "ArcaneCore.Game/Quests/Adapters/DbScriptQuestCredit.cs",
    ];

    /// <summary>The one place the relay step command is switched on.</summary>
    private static readonly string[] RelayCommandSwitchFiles = ["ArcaneCore.Game/Creatures/CreatureMapSystem.RelayScripts.cs"];

    [Fact]
    public void ScriptCommandNames_AppearOnlyInTheKnownRelayFiles()
        => AssertSites(Sites(@"SCRIPT_COMMAND_"), ScriptCommandFiles, "names a SCRIPT_COMMAND_*");

    [Fact]
    public void TheRelayCommandSwitch_IsOnlyInTheRelayRunner()
        => AssertSites(Sites(@"switch \(step\.Command\)|step\.Command (==|is) "), RelayCommandSwitchFiles, "switches on a relay step command");

    [Fact]
    public void EventAiHandlers_AreDeclaredOnlyUnderTheEventAiFolder()
    {
        string[] sites = Sites(@"class \w+ *(\(.*\))? *: *EventAi(Action|Event)Handler");

        Assert.NotEmpty(sites);
        Assert.All(sites, site => Assert.StartsWith("ArcaneCore.Game/Creatures/AI/EventAi/", site, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryEventAiHandler_IsConverted_OrNamedHereAsNotConverted()
    {
        EventAiRegistry registry = EventAiRegistry.Default;
        Assert.NotEmpty(registry.EventTypes);
        Assert.NotEmpty(registry.ActionTypes);
        byte[] events = [.. registry.EventTypes.Except(EventAiRuleConverter.Events.Keys).Order()];
        byte[] actions = [.. registry.ActionTypes.Except(EventAiRuleConverter.Actions.Keys).Order()];

        // Not converted on purpose (docs/reference/rules-conversion.md): add a type to the converter or to this list, never leave it out.
        Assert.Equal(NotConvertedEvents, events);
        Assert.Equal(NotConvertedActions, actions);
    }

    private static readonly byte[] NotConvertedEvents = [];

    private static readonly byte[] NotConvertedActions = [];

    private static void AssertSites(string[] found, string[] expected, string what)
    {
        string[] added = [.. found.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] gone = [.. expected.Except(found, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        Assert.True(added.Length == 0, $"New file(s) that {what} (route the behaviour through a rule host instead): {string.Join(", ", added)}");
        Assert.True(gone.Length == 0, $"File(s) that no longer {what} (remove them from the pin): {string.Join(", ", gone)}");
    }

    /// <summary>The source files under <c>src</c> (relative, forward slashes) with a line matching <paramref name="pattern"/>.</summary>
    private static string[] Sites(string pattern)
    {
        string src = SourceRoot();
        var regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return [.. Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadLines(path).Any(regex.IsMatch))
            .Select(path => Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];
    }

    private static string SourceRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "ArcaneCore.Game");
            if (Directory.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }

        throw new DirectoryNotFoundException("The src folder was not found above " + AppContext.BaseDirectory);
    }
}

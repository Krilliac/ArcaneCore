using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using System.Runtime.CompilerServices;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Inspect;

/// <summary>Extra <c>.inspect unit|ai|motion|map|threat</c> sections registered from outside InspectCommands (I13).</summary>
public sealed class InspectSectionRegistryTests
{
    [Fact]
    public void SectionsComeBackInOrderThenNameAndCanBeRemoved()
    {
        using IDisposable late = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Motion, "zz-late", (_, _) => null, Order: 5));
        using IDisposable b = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Motion, "b-tie", (_, _) => null, Order: 1));
        using IDisposable a = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Motion, "A-tie", (_, _) => null, Order: 1));
        using IDisposable other = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Ai, "ai-only", (_, _) => null));

        Assert.Equal(["A-tie", "b-tie", "zz-late"], Mine(InspectLeaf.Motion));
        Assert.Equal(["ai-only"], Mine(InspectLeaf.Ai));

        b.Dispose();
        b.Dispose(); // idempotent
        Assert.Equal(["A-tie", "zz-late"], Mine(InspectLeaf.Motion));
    }

    /// <summary>This test's sections only: the registry is process-wide and other batches register their own.</summary>
    private static string[] Mine(InspectLeaf leaf)
        => [.. InspectSectionRegistry.SectionsOf(leaf).Select(s => s.Name).Where(n => n is "A-tie" or "b-tie" or "zz-late" or "ai-only")];

    [Fact]
    public void ASecondSectionOfTheSameLeafAndNameIsRefused()
    {
        using IDisposable first = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Threat, "dup", (_, _) => null));

        Assert.Throws<InvalidOperationException>(() => InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Threat, "DUP", (_, _) => null)));
        using IDisposable otherLeaf = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Map, "dup", (_, _) => null));
        Assert.Throws<ArgumentException>(() => InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Map, " ", (_, _) => null)));
    }

    [Fact]
    public void TheLeafNamesAreTheInspectSubcommands()
    {
        Assert.Equal(["unit", "ai", "motion", "map", "threat"], Enum.GetValues<InspectLeaf>().Select(InspectSectionRegistry.LeafName));
    }

    [Fact]
    public async Task EveryHookedLeafRendersItsRegisteredSections()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPSECT", "Inspsect", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await host.OnWorldAsync(() => host.WorldServices.GetService<CreatureWorldFeature>()!.FindSystem(0)!.Creatures.Last());
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Inspsect")!.Selection = wolf.Guid);

        var seen = new List<(InspectLeaf Leaf, bool HasUnit, bool HasMap)>();
        var registrations = new List<IDisposable>();
        foreach (InspectLeaf leaf in Enum.GetValues<InspectLeaf>())
        {
            InspectLeaf captured = leaf;
            registrations.Add(InspectSectionRegistry.Register(new InspectSection(leaf, "probe-" + InspectSectionRegistry.LeafName(leaf), (context, subject) =>
            {
                // The registry is process-wide: other tests' inspect commands run these sections too, and must see nothing.
                if (context.InvokerName != "Inspsect")
                {
                    return null;
                }

                seen.Add((captured, subject.Unit is not null, subject.Map is not null));
                return ["probe line for " + InspectSectionRegistry.LeafName(captured)];
            })));
        }

        try
        {
            foreach (string leaf in new[] { "unit", "ai", "motion", "map", "threat" })
            {
                string[] lines = await RunAsync(gm, ".inspect " + leaf);
                int heading = Array.IndexOf(lines, "[probe-" + leaf + "]");
                Assert.True(heading >= 0, $".inspect {leaf} printed no probe heading: {string.Join(" | ", lines)}");
                Assert.Equal("probe line for " + leaf, lines[heading + 1]);
                Assert.Equal(lines.Length, heading + 2); // the sections come after the leaf's own lines
            }
        }
        finally
        {
            foreach (IDisposable registration in registrations) registration.Dispose();
        }

        Assert.Equal(
            [(InspectLeaf.Unit, true, true), (InspectLeaf.Ai, true, true), (InspectLeaf.Motion, true, true), (InspectLeaf.Map, false, true), (InspectLeaf.Threat, true, true)],
            seen);

        // Removed again: no heading.
        Assert.DoesNotContain(await RunAsync(gm, ".inspect unit"), l => l.StartsWith("[probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingSectionNamesItselfAndTheOthersStillRender()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPFAIL", "Inspfail", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        using IDisposable boom = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "boom", (context, _) => context.InvokerName == "Inspfail" ? throw new InvalidOperationException("secret detail") : null, Order: 1));
        using IDisposable empty = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "empty", (_, _) => [], Order: 2));
        using IDisposable fine = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "fine", (context, subject) => context.InvokerName == "Inspfail" ? [$"for {subject.Unit?.GetType().Name}"] : null, Order: 3));

        string[] lines = await RunAsync(gm, ".inspect unit");

        Assert.Equal("[boom] failed: InvalidOperationException", lines[^3]);
        Assert.Equal("[fine]", lines[^2]);
        Assert.Equal("for Player", lines[^1]);
        Assert.DoesNotContain(lines, l => l.Contains("empty", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("secret detail", StringComparison.Ordinal));
    }

    private static CommandContext Console(List<string> output)
        => CommandContext.ForConsole((WorldSession)RuntimeHelpers.GetUninitializedObject(typeof(WorldSession)), new CommandTable([]), output.Add);

    /// <summary>The registry is process-wide: these sections answer a console context only, so other tests' inspect output is unchanged.</summary>
    private static InspectSectionRenderer ConsoleOnly(Func<IEnumerable<string>?> body) => (context, _) => context.IsConsole ? body() : null;

    private static IEnumerable<string> FailsHalfway()
    {
        yield return "partial line";
        throw new InvalidOperationException("halfway");
    }

    [Fact]
    public void RenderPrintsAHeadingThenLinesInOrderAndSkipsEmptySections()
    {
        var output = new List<string>();
        using IDisposable second = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Threat, "rt-second", ConsoleOnly(() => ["two"]), Order: 2));
        using IDisposable first = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Threat, "rt-first", ConsoleOnly(() => ["one-a", "one-b"]), Order: 1));
        using IDisposable nothing = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Threat, "rt-nothing", ConsoleOnly(() => null), Order: 3));

        InspectSectionRegistry.Render(Console(output), InspectLeaf.Threat, default);

        Assert.Equal(["[rt-first]", "one-a", "one-b", "[rt-second]", "two"], output.SkipWhile(l => l != "[rt-first]").ToArray());
        Assert.DoesNotContain("[rt-nothing]", output);
    }

    [Fact]
    public void ASectionOnlyRendersForItsOwnLeaf()
    {
        var output = new List<string>();
        using IDisposable only = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Motion, "rt-motion-only", ConsoleOnly(() => ["m"])));

        InspectSectionRegistry.Render(Console(output), InspectLeaf.Ai, default);

        Assert.DoesNotContain("[rt-motion-only]", output);
    }

    [Fact]
    public void ASectionThatThrowsHalfwayPrintsNoPartialLines()
    {
        var output = new List<string>();
        using IDisposable halfway = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Map, "rt-halfway", ConsoleOnly(FailsHalfway)));

        InspectSectionRegistry.Render(Console(output), InspectLeaf.Map, default);

        Assert.Contains("[rt-halfway] failed: InvalidOperationException", output);
        Assert.DoesNotContain("partial line", output);
        Assert.DoesNotContain("[rt-halfway]", output);
    }

    [Fact]
    public void ARemovedSectionNoLongerRendersAndItsNameIsFreeAgain()
    {
        var output = new List<string>();
        IDisposable registration = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "rt-reuse", ConsoleOnly(() => ["x"])));
        registration.Dispose();

        InspectSectionRegistry.Render(Console(output), InspectLeaf.Unit, default);
        Assert.DoesNotContain("[rt-reuse]", output);

        using IDisposable again = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "rt-reuse", ConsoleOnly(() => ["y"])));
        output.Clear();
        InspectSectionRegistry.Render(Console(output), InspectLeaf.Unit, default);
        Assert.Contains("[rt-reuse]", output);
    }

    [Fact]
    public void RegisterAndRenderRefuseNull()
    {
        Assert.Throws<ArgumentNullException>(() => InspectSectionRegistry.Register(null!));
        Assert.Throws<ArgumentNullException>(() => InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Unit, "rt-nullrender", null!)));
        Assert.Throws<ArgumentNullException>(() => InspectSectionRegistry.Render(null!, InspectLeaf.Unit, default));
    }

    [Fact]
    public void ConcurrentRegistrationsAllLandAndAllLeave()
    {
        const int Count = 64;
        var registrations = new IDisposable[Count];

        Parallel.For(0, Count, i =>
            registrations[i] = InspectSectionRegistry.Register(new InspectSection(InspectLeaf.Map, "rt-par-" + i, (_, _) => null)));
        Assert.Equal(Count, InspectSectionRegistry.SectionsOf(InspectLeaf.Map).Count(s => s.Name.StartsWith("rt-par-", StringComparison.Ordinal)));

        Parallel.ForEach(registrations, r => r.Dispose());
        Assert.Equal(0, InspectSectionRegistry.SectionsOf(InspectLeaf.Map).Count(s => s.Name.StartsWith("rt-par-", StringComparison.Ordinal)));
    }

    private static async Task<string[]> RunAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        return await gm.CollectChatLinesAsync();
    }
}

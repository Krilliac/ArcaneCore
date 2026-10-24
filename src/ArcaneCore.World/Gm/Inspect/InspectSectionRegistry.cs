using System.Collections.Immutable;
using ArcaneCore.Kernel.Reflection;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Gm.Inspect;

/// <summary>
/// The registry the inspect leaves read. Sections come from every <see cref="IInspectSection"/> class with a public parameterless
/// constructor in the built-in assemblies (discovered once, on first use) plus <see cref="Register"/>ed ones (hot-loaded packs, tests).
/// Thread safe; the section list is an immutable snapshot swapped under a lock, so a leaf renders against a stable list. A discovered
/// section that cannot be created, has no name or repeats a leaf and name is skipped and listed in <see cref="DiscoveryProblems"/>
/// (a test asserts the list is empty), so a bad contribution never breaks every <c>.inspect</c> leaf at run time.
/// </summary>
public static class InspectSectionRegistry
{
    private static readonly object Gate = new();
    private static ImmutableArray<IInspectSection> _registered = [];
    private static ImmutableArray<IInspectSection>? _discovered;
    private static IReadOnlyList<string> _problems = [];

    /// <summary>The leaf name as typed after <c>.inspect</c>.</summary>
    public static string LeafName(InspectLeaf leaf) => leaf.ToString().ToLowerInvariant();

    /// <summary>What discovery of the built-in assemblies skipped (empty when every contribution was accepted).</summary>
    public static IReadOnlyList<string> DiscoveryProblems
    {
        get
        {
            lock (Gate)
            {
                _ = All();
                return _problems;
            }
        }
    }

    /// <summary>Add a section; dispose the result to remove it. A second section of the same leaf and name (a discovered one included) is refused.</summary>
    public static IDisposable Register(IInspectSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(section.Name);
        if (section is InspectSection { Renderer: null })
        {
            throw new ArgumentNullException(nameof(section), "the section has no renderer");
        }

        lock (Gate)
        {
            if (All().Any(s => Same(s, section)))
            {
                throw new InvalidOperationException($"inspect {LeafName(section.Leaf)} already has a section '{section.Name}'");
            }

            _registered = _registered.Add(section);
        }

        return new Registration(section);
    }

    /// <summary>The sections of <paramref name="leaf"/> in render order.</summary>
    public static IReadOnlyList<IInspectSection> SectionsOf(InspectLeaf leaf)
    {
        lock (Gate)
        {
            return [.. All().Where(s => s.Leaf == leaf).OrderBy(s => s.Order).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Print the registered sections of <paramref name="leaf"/> after the leaf's own lines: a <c>[name]</c> heading then the section's lines.
    /// A section that returns nothing prints nothing; one that throws prints one failure line and the rest still render.
    /// </summary>
    public static void Render(CommandContext context, InspectLeaf leaf, InspectSubject subject)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (IInspectSection section in SectionsOf(leaf))
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = [.. section.Render(context, subject) ?? []];
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The chat line stays terse; the exception goes to the log and the fault ledger.
                CommandFaultLogging.Log(context, "gm.inspect-section", error, $"inspect {LeafName(leaf)} [{section.Name}]");
                context.Reply($"[{section.Name}] failed: {error.GetType().Name}");
                continue;
            }

            if (lines.Count == 0)
            {
                continue;
            }

            context.Reply($"[{section.Name}]");
            foreach (string line in lines)
            {
                context.Reply(line);
            }
        }
    }

    /// <summary>
    /// The sections of the <see cref="IInspectSection"/> classes of <paramref name="assemblies"/> (public parameterless constructor), and
    /// what was skipped: a class that cannot be created, a blank name, a repeated leaf and name (the first by type name wins).
    /// </summary>
    internal static (ImmutableArray<IInspectSection> Sections, IReadOnlyList<string> Problems) Discover(CodeAssemblies assemblies)
    {
        var found = new List<IInspectSection>();
        var problems = new List<string>();
        foreach (Type type in AssemblyDiscovery.FindTypes<IInspectSection>(assemblies).Where(t => t.GetConstructor(Type.EmptyTypes) is not null))
        {
            IInspectSection section;
            try
            {
                section = (IInspectSection)Activator.CreateInstance(type)!;
                if (string.IsNullOrWhiteSpace(section.Name))
                {
                    problems.Add($"{type.FullName}: the section has no name");
                    continue;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                problems.Add($"{type.FullName}: could not be created ({error.GetType().Name})");
                continue;
            }

            if (found.Any(s => Same(s, section)))
            {
                problems.Add($"{type.FullName}: inspect {LeafName(section.Leaf)} already has a section '{section.Name}'");
                continue;
            }

            found.Add(section);
        }

        return ([.. found], problems);
    }

    private static bool Same(IInspectSection a, IInspectSection b)
        => a.Leaf == b.Leaf && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

    private static ImmutableArray<IInspectSection> All()
    {
        if (_discovered is null)
        {
            try
            {
                (ImmutableArray<IInspectSection> sections, IReadOnlyList<string> problems) = Discover(AssemblyDiscovery.BuiltIn);
                _discovered = sections;
                _problems = problems;
            }
            catch (CodeAssemblyLoadException error)
            {
                _discovered = [];
                _problems = [$"discovery failed: {error.Message}"];
            }
        }

        return _discovered.Value.AddRange(_registered);
    }

    private sealed class Registration(IInspectSection section) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (Gate)
            {
                _registered = _registered.Remove(section);
            }
        }
    }
}

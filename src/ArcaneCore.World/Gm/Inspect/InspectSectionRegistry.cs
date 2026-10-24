using System.Collections.Immutable;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Gm.Inspect;

/// <summary>The <c>.inspect</c> leaves that render registered extra sections (I13).</summary>
public enum InspectLeaf
{
    Unit,
    Ai,
    Motion,
    Map,
    Threat,
}

/// <summary>What an inspect leaf is showing: the unit (<see cref="InspectLeaf.Unit"/>, <see cref="InspectLeaf.Ai"/>, <see cref="InspectLeaf.Motion"/>, <see cref="InspectLeaf.Threat"/>) or the map (<see cref="InspectLeaf.Map"/>).</summary>
public readonly record struct InspectSubject(Unit? Unit, Map? Map);

/// <summary>
/// Renders one extra section of an inspect leaf. Returns the chat lines (empty or null to print nothing). Runs on the world thread after
/// the leaf's own lines, read-only.
/// </summary>
public delegate IEnumerable<string>? InspectSectionRenderer(CommandContext context, InspectSubject subject);

/// <summary>One extra section of an inspect leaf.</summary>
/// <param name="Leaf">The leaf it extends.</param>
/// <param name="Name">The section name (unique per leaf, case-insensitive); printed as a heading line when the section has lines.</param>
/// <param name="Render">The renderer.</param>
/// <param name="Order">Sort key within a leaf (lower first); ties sort by name.</param>
public sealed record InspectSection(InspectLeaf Leaf, string Name, InspectSectionRenderer Render, int Order = 0);

/// <summary>
/// Contributes sections to <c>.inspect unit|ai|motion|map|threat</c> from the contributor's own file (I13): an implementation with a
/// parameterless constructor in a built-in assembly is discovered like an <see cref="ArcaneCore.World.Gm.Core.ICommandExtension"/>, so batches add sections
/// without editing <c>InspectCommands.cs</c>.
/// </summary>
public interface IInspectSectionProvider
{
    IReadOnlyList<InspectSection> Sections { get; }
}

/// <summary>
/// The registry the inspect leaves read. Sections come from every <see cref="IInspectSectionProvider"/> (discovered once) plus
/// <see cref="Register"/>ed ones (hot-loaded packs, tests). Thread safe; the section list is an immutable snapshot swapped under a lock, so a
/// leaf renders against a stable list.
/// </summary>
public static class InspectSectionRegistry
{
    private static readonly object Gate = new();
    private static ImmutableArray<InspectSection> _registered = [];
    private static ImmutableArray<InspectSection>? _discovered;

    /// <summary>The leaf name as typed after <c>.inspect</c>.</summary>
    public static string LeafName(InspectLeaf leaf) => leaf.ToString().ToLowerInvariant();

    /// <summary>Add a section; dispose the result to remove it. A second section of the same leaf and name (a discovered one included) is refused.</summary>
    public static IDisposable Register(InspectSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(section.Name);
        ArgumentNullException.ThrowIfNull(section.Render);
        lock (Gate)
        {
            if (All().Any(s => s.Leaf == section.Leaf && s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"inspect {LeafName(section.Leaf)} already has a section '{section.Name}'");
            }

            _registered = _registered.Add(section);
        }

        return new Registration(section);
    }

    /// <summary>The sections of <paramref name="leaf"/> in render order.</summary>
    public static IReadOnlyList<InspectSection> SectionsOf(InspectLeaf leaf)
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
        foreach (InspectSection section in SectionsOf(leaf))
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = [.. section.Render(context, subject) ?? []];
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
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

    private static ImmutableArray<InspectSection> All()
    {
        _discovered ??= Discover();
        return _discovered.Value.AddRange(_registered);
    }

    private static ImmutableArray<InspectSection> Discover()
    {
        var found = new List<InspectSection>();
        foreach (IInspectSectionProvider provider in AssemblyDiscovery.CreateAll<IInspectSectionProvider>())
        {
            foreach (InspectSection section in provider.Sections)
            {
                if (found.Any(s => s.Leaf == section.Leaf && s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"inspect {LeafName(section.Leaf)} has two sections named '{section.Name}'");
                }

                found.Add(section);
            }
        }

        return [.. found];
    }

    private sealed class Registration(InspectSection section) : IDisposable
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

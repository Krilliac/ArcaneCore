using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Commands;

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
/// One extra section of an <c>.inspect unit|ai|motion|map|threat</c> leaf (I13). A class with a public parameterless constructor in a
/// built-in assembly is discovered like an <see cref="ArcaneCore.World.Gm.Core.ICommandExtension"/>, so a batch adds its section from its
/// own file without editing <c>InspectCommands.cs</c>. <see cref="InspectSectionRegistry.Register"/> adds one at run time (hot-loaded packs, tests).
/// </summary>
public interface IInspectSection
{
    /// <summary>The leaf it extends.</summary>
    InspectLeaf Leaf { get; }

    /// <summary>The section name (unique per leaf, case-insensitive); printed as a heading line when the section has lines.</summary>
    string Name { get; }

    /// <summary>Sort key within a leaf (lower first); ties sort by name.</summary>
    int Order => 0;

    /// <summary>The chat lines of the section (empty or null to print nothing). World thread, after the leaf's own lines, read-only.</summary>
    IEnumerable<string>? Render(CommandContext context, InspectSubject subject);
}

/// <summary>Renders one extra section of an inspect leaf (see <see cref="InspectSection"/>).</summary>
public delegate IEnumerable<string>? InspectSectionRenderer(CommandContext context, InspectSubject subject);

/// <summary>An <see cref="IInspectSection"/> built from a delegate, for <see cref="InspectSectionRegistry.Register"/>.</summary>
/// <param name="Leaf">The leaf it extends.</param>
/// <param name="Name">The section name (unique per leaf, case-insensitive).</param>
/// <param name="Renderer">The renderer.</param>
/// <param name="Order">Sort key within a leaf (lower first); ties sort by name.</param>
public sealed record InspectSection(InspectLeaf Leaf, string Name, InspectSectionRenderer Renderer, int Order = 0) : IInspectSection
{
    public IEnumerable<string>? Render(CommandContext context, InspectSubject subject) => Renderer(context, subject);
}

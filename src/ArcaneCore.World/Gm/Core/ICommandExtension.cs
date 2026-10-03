using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// Adds sub-commands under a root (or nested command) another feature defined, so several
/// lanes can contribute to one retail root (<c>.modify</c>, <c>.lookup</c>, <c>.tele</c>...)
/// without the duplicate-root failure of <see cref="ICommandGroup"/>. Every non-abstract
/// implementation in this assembly (parameterless constructor) is discovered. The table fails at
/// startup when <see cref="Path"/> names no command or a child name already exists there.
/// </summary>
public interface ICommandExtension
{
    /// <summary>The command to extend, words separated by spaces (e.g. "modify" or "lookup player").</summary>
    string Path { get; }

    /// <summary>The sub-commands to append, in order.</summary>
    IReadOnlyList<ChatCommand> Children { get; }
}

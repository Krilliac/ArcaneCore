using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Commands;

/// <summary>
/// A feature's root chat commands (e.g. <c>.tele</c>, <c>.go</c>). Every non-abstract
/// implementation in this assembly is discovered (parameterless constructor) and its roots are
/// appended after the built-in M6 commands, groups ordered by full type name, so abbreviations
/// that resolve today keep resolving the same way (vmangos FindCommand: first match in table
/// order). Two roots with the same name anywhere in the table fail at startup; to add
/// sub-commands to a root another feature owns use <see cref="ICommandExtension"/>.
/// </summary>
public interface ICommandGroup
{
    IReadOnlyList<ChatCommand> Commands { get; }
}

/// <summary>The daemon's command table: <see cref="BuiltinCommands"/>, then every <see cref="ICommandGroup"/>, then every <see cref="ICommandExtension"/>.</summary>
public static class ChatCommands
{
    public static CommandTable CreateTable(GmOptions? options = null)
        => Build(
            BuiltinCommands.Create().Roots,
            AssemblyDiscovery.CreateAll<ICommandGroup>(),
            AssemblyDiscovery.CreateAll<ICommandExtension>(),
            options ?? new GmOptions());

    /// <summary>The table for the daemon's configuration (<c>World:GmCommands</c>).</summary>
    public static CommandTable CreateTable(IConfiguration configuration) => CreateTable(GmOptions.Bind(configuration));

    /// <summary>Assemble a table from explicit parts (the discovery-free form tests use).</summary>
    public static CommandTable Build(
        IEnumerable<ChatCommand> builtins,
        IEnumerable<ICommandGroup> groups,
        IEnumerable<ICommandExtension> extensions,
        GmOptions options)
    {
        var roots = new List<ChatCommand>(builtins);
        foreach (ICommandGroup group in groups)
        {
            roots.AddRange(group.Commands);
        }

        string? duplicate = roots.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"chat command '.{duplicate}' is defined twice");
        }

        if (options.RetailLevels)
        {
            roots = RetailCommandLevels.Apply(roots);
        }

        // Retail table order decides what an abbreviation means (Chat.cpp:1185-1366); the pre-retail
        // exact-name-first rule keeps the registration order (built-ins, then groups by type name).
        if (!options.ExactNameFirst)
        {
            roots = RetailCommandOrder.Sort(roots, r => r.Name);
        }

        foreach (ICommandExtension extension in extensions)
        {
            string[] path = extension.Path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 0)
            {
                throw new InvalidOperationException($"{extension.GetType().Name} extends no command (empty path)");
            }

            ApplyExtension(roots, path, 0, extension);
        }

        return new CommandTable(roots, options);
    }

    private static void ApplyExtension(List<ChatCommand> level, string[] path, int depth, ICommandExtension extension)
    {
        int index = level.FindIndex(c => c.Name.Equals(path[depth], StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"command extension {extension.GetType().Name} targets '.{string.Join(' ', path)}' but no command '{path[depth]}' exists there");
        }

        ChatCommand target = level[index];
        var children = new List<ChatCommand>(target.SubCommands);
        if (depth + 1 < path.Length)
        {
            ApplyExtension(children, path, depth + 1, extension);
        }
        else
        {
            foreach (ChatCommand child in extension.Children)
            {
                if (children.Any(c => c.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"chat command '.{string.Join(' ', path)} {child.Name}' is defined twice (extension {extension.GetType().Name})");
                }

                children.Add(child);
            }
        }

        level[index] = target with { Children = children };
    }
}

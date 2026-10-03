using ArcaneCore.World.Features;

namespace ArcaneCore.World.Commands;

/// <summary>
/// A feature's root chat commands (e.g. <c>.tele</c>, <c>.go</c>). Every non-abstract
/// implementation in this assembly is discovered (parameterless constructor) and its roots are
/// appended after the built-in M6 commands, groups ordered by full type name, so abbreviations
/// that resolve today keep resolving the same way (vmangos FindCommand: first match in table
/// order). Two roots with the same name anywhere in the table fail at startup.
/// </summary>
public interface ICommandGroup
{
    IReadOnlyList<ChatCommand> Commands { get; }
}

/// <summary>The daemon's command table: <see cref="BuiltinCommands"/>, then every <see cref="ICommandGroup"/>.</summary>
public static class ChatCommands
{
    public static CommandTable CreateTable()
    {
        var roots = new List<ChatCommand>(BuiltinCommands.Create().Roots);
        foreach (ICommandGroup group in AssemblyDiscovery.CreateAll<ICommandGroup>())
        {
            roots.AddRange(group.Commands);
        }

        string? duplicate = roots.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"chat command '.{duplicate}' is defined twice");
        }

        return new CommandTable(roots);
    }
}

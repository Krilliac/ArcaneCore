using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace GmCommands;

/// <summary>
/// The smallest useful module: one GM chat command. The server finds every public, non-abstract class with a
/// public parameterless constructor that implements <see cref="ICommandGroup"/> (chat commands) and/or
/// IOpcodeHandlerGroup (packet handlers). A module cannot replace a built-in or another module's command or
/// opcode (the whole module is rejected), and a root must not be a prefix of an existing one.
/// </summary>
public sealed class GmCommandGroup : ICommandGroup
{
    // Bump this text, rebuild, and `.hotmodule reload GmCommands` shows the new behaviour without a restart.
    private const string Version = "v1";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "modhello",
            AccountSecurity.GameMaster,
            "Syntax: .modhello [text] - answers with the module version and your character name (module template).",
            Hello),
    ];

    private static bool Hello(CommandContext context, string args)
    {
        string text = args.Length == 0 ? "hello" : args;
        context.Reply($"GmCommands {Version}: {text}, {context.Player.Name}");
        return true;
    }
}

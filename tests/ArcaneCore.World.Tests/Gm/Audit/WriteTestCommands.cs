using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>Binds a command to a context the way <see cref="CommandTable.Execute"/> does, for tests that call <c>DebugWrite.Record</c> without running a line.</summary>
internal static class WriteTestCommands
{
    /// <summary>Bind a command named after the last word of <paramref name="path"/>, marked <see cref="ChatCommand.IsWrite"/> unless <paramref name="isWrite"/> is false.</summary>
    public static CommandContext Bind(CommandContext context, string path, bool isWrite = true)
    {
        string leaf = path[(path.LastIndexOf(' ') + 1)..];
        context.Bind(new ChatCommand(leaf, AccountSecurity.Administrator, "test", null, null, null, isWrite), path);
        return context;
    }
}

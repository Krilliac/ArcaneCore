using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>
/// <c>.hotmodule list | load | reload | unload &lt;name&gt;</c>, Administrator only, present only
/// when <c>World:HotCode:Modules:Enabled</c> is set. The name selects a folder the operator put
/// under the module directory; no command takes a path or any code.
/// </summary>
public static class HotModuleCommands
{
    public const string RootName = "hotmodule";

    public static ChatCommand Create(ModuleHost host, HotCodeAudit audit)
        => new(RootName, AccountSecurity.Administrator, "Hot-loaded code modules (opt-in).", Children:
        [
            new ChatCommand("list", AccountSecurity.Administrator, "Syntax: .hotmodule list — the loaded modules.",
                (context, args) =>
                {
                    context.Reply(Format(host));
                    return true;
                }),
            Verb("load", "Syntax: .hotmodule load <name> — load <directory>/<name>/<name>.dll.", host, audit, host.LoadAsync),
            Verb("reload", "Syntax: .hotmodule reload <name> — build the new version, then swap it for the loaded one (the old one stays if the new one is bad).", host, audit, host.ReloadAsync),
            Verb("unload", "Syntax: .hotmodule unload <name> — remove the module's handlers and commands and release it.", host, audit, host.UnloadAsync),
        ]);

    public static string Format(ModuleHost host)
    {
        IReadOnlyList<ModuleInfo> modules = host.List();
        string lines = modules.Count == 0
            ? "No modules loaded."
            : string.Join('\n', modules.Select(m => $"{m.Name}: {m.Opcodes} opcode handlers, commands [{string.Join(", ", m.CommandRoots.Select(r => "." + r))}], loaded {m.LoadedUtc:u}, sha256 {m.Sha256[..16]}..."));
        return $"{lines}\nUnloaded modules not yet freed by the runtime: {host.RetiredStillReferenced}";
    }

    private static ChatCommand Verb(string verb, string help, ModuleHost host, HotCodeAudit audit, Func<string, Task<ModuleResult>> run)
        => new(verb, AccountSecurity.Administrator, help, (context, args) =>
        {
            string name = args.Trim();
            if (name.Length == 0 || name.Contains(' '))
            {
                return false;
            }

            string who = context.Player.Name;
            audit.Record("hotmodule-command", $"{who} {verb} {name}");
            Task<ModuleResult> task = run(name);

            // The commit is posted to this very (world) thread: never block on it. Answer from the next tick.
            _ = task.ContinueWith(
                finished => context.World.Post(() => context.Reply(finished.IsCompletedSuccessfully
                    ? $"Module {verb} {(finished.Result.Ok ? "done" : "rejected")}: {finished.Result.Detail}"
                    : $"Module {verb} failed: {finished.Exception?.GetBaseException().Message}")),
                TaskScheduler.Default);
            context.Reply($"Module {verb} requested.");
            return true;
        });
}

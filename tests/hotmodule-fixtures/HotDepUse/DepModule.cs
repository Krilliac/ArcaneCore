using System.Reflection;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;

namespace HotDepModule;

/// <summary>
/// A public module type that needs a private dependency (assembly HotDepLib, a dll the host tests write next to the module).
/// The type loads the dependency when it is constructed, through the module's own load context, which is what makes the
/// allowlist decision visible: HotDepUse lets a refusal escape, HotDepSwallow (the same source with SWALLOW) catches it.
/// </summary>
public sealed class DepCommands : ICommandGroup
{
    public DepCommands()
    {
#if SWALLOW
        try
        {
            LoadDependency();
        }
        catch (Exception)
        {
            // deliberately swallowed: the host must still reject the module because the load context recorded a refusal
        }
#else
        LoadDependency();
#endif
    }

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("hotdep", AccountSecurity.Player, "hotdep module", (_, _) => true),
    ];

    private static void LoadDependency() => Assembly.Load(new AssemblyName("HotDepLib"));
}

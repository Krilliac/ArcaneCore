using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;

namespace HotFixture;

/// <summary>What the host tests read back to tell the versions apart.</summary>
public static class Marker
{
#if V2
    public const int Version = 2;
#else
    public const int Version = 1;
#endif
}

public sealed class FixtureOpcodes : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
#if V2
        table.OnSession(WorldOpcode.CmsgTutorialFlag, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
#endif
    }
}

public sealed class FixtureCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
#if V2
        new ChatCommand("hotfixture", AccountSecurity.Player, "fixture v2", (_, _) => true),
        new ChatCommand("hotfixtureextra", AccountSecurity.Player, "fixture v2 extra", (_, _) => true),
#else
        new ChatCommand("hotfixture", AccountSecurity.Player, "fixture v1", (_, _) => true),
#endif
    ];
}

/// <summary>Not public: never picked up (a module's internals are not extension points).</summary>
internal sealed class HiddenGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

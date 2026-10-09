using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Gm.DebugDraw;

/// <summary>Administrator-only extension of the existing .debug root.</summary>
public sealed class PacketCaptureCommands : ICommandExtension
{
    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("capture", AccountSecurity.Administrator,
            "Syntax: .debug capture on|off <player>\nStart or stop an opt-in PKT 3.1 trace for an online player's client session.", Capture, RetailLevel: 6),
    ];

    private static bool Capture(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length != 2 || words[0] is not ("on" or "off")) return false;
        var target = context.World.FindOnlinePlayer(words[1]);
        if (target is null)
        {
            context.Reply("Player is not online.");
            return true;
        }

        WorldSession? session = context.Session.Services.GetService(typeof(SessionRegistry)) is SessionRegistry registry
            ? registry.Find(target.AccountId) : target.Session as WorldSession;
        if (session is null || !ReferenceEquals(session.Player, target))
        {
            context.Reply("Player has no live client session.");
            return true;
        }

        string message;
        if (words[0] == "on") session.StartPacketCapture(out message);
        else session.StopPacketCapture(out message);
        context.Reply(message);
        return true;
    }
}

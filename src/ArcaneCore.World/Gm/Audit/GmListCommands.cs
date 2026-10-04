using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// <c>.gm ingame</c> and <c>.gm list</c>, added under the built-in <c>.gm</c> root. <c>ingame</c> needs Moderator (TrinityCore 1;
/// mangos-zero and acore open it to everybody, which would also put <c>gm ...</c> into every player's <c>.commands</c>): the
/// online players whose GM mode is on, with whether they take whispers (mangos-zero GMCommands.cpp:469-500). ArcaneCore has no GM-invisibility state, so there is nothing to hide
/// (<c>.gm visible</c> is not provided). <c>list</c> needs Administrator (the cores use 3) and shows every staff account
/// that is ONLINE, GM mode or not; the cores' <c>.gm list</c> reads the account table, which this server's account seam cannot
/// list yet (docs/integration/gm-audit-lane.md).
/// </summary>
public sealed class GmListCommands : ICommandExtension
{
    public string Path => "gm";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("ingame", AccountSecurity.Moderator, "Syntax: .gm ingame\nList the game masters online with their GM mode on, and whether they accept whispers.", Ingame),
        new ChatCommand("list", AccountSecurity.Administrator, "Syntax: .gm list\nList every staff member online with their security level.", List),
    ];

    private static bool Ingame(CommandContext context, string text)
    {
        if (text.Length > 0)
        {
            return false;
        }

        ChatFeature chat = context.Session.Services.GetRequiredService<ChatFeature>();
        Player[] gms = [.. context.World.OnlinePlayers.Where(p => p.IsGameMaster).OrderBy(p => p.Name, StringComparer.Ordinal)];
        if (gms.Length == 0)
        {
            context.Reply(GmAuditStrings.GmsNotLogged);
            return true;
        }

        context.Reply(GmAuditStrings.GmsOnServer);
        foreach (Player gm in gms)
        {
            context.Reply(GmAuditStrings.GmIngameLine(GmStrings.PlayerLink(gm.Name), chat.AcceptsWhispers(gm)));
        }

        return true;
    }

    private static bool List(CommandContext context, string text)
    {
        if (text.Length > 0)
        {
            return false;
        }

        Player[] staff = [.. context.World.OnlinePlayers.Where(p => p.Security > AccountSecurity.Player).OrderByDescending(p => p.Security).ThenBy(p => p.Name, StringComparer.Ordinal)];
        if (staff.Length == 0)
        {
            context.Reply(GmAuditStrings.NoStaffOnline);
            return true;
        }

        context.Reply(GmAuditStrings.StaffOnline);
        foreach (Player member in staff)
        {
            context.Reply(GmAuditStrings.StaffLine(GmStrings.PlayerLink(member.Name), member.Security, member.IsGameMaster));
        }

        return true;
    }
}

using ArcaneCore.Kernel.Accounts;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The <c>World:GmCommands</c> configuration section. Every key's default is the retail
/// behaviour (vmangos) unless the key's own comment says otherwise.
/// </summary>
public sealed class GmOptions
{
    public const string SectionName = "World:GmCommands";

    /// <summary>
    /// The retail account level (vmangos AccountTypes, D:\refs\vmangos\src\shared\Common.h:136-146:
    /// PLAYER 0, MODERATOR 1, TICKETMASTER 2, GAMEMASTER 3, BASIC_ADMIN 4, DEVELOPER 5,
    /// ADMINISTRATOR 6, CONSOLE 7) each stored <see cref="AccountSecurity"/> stands for.
    /// ArcaneCore stores four levels, so the retail levels 2, 4 and 5 are only reachable by
    /// mapping a stored level onto them (an operator may remap, e.g. GameMaster=4).
    /// </summary>
    public Dictionary<AccountSecurity, byte> SecurityMap { get; } = new()
    {
        [AccountSecurity.Player] = 0,
        [AccountSecurity.Moderator] = 1,
        [AccountSecurity.GameMaster] = 3,
        [AccountSecurity.Administrator] = 6,
    };

    /// <summary>Write one log line per GM command (a command above level 0), as vmangos Chat.cpp:1908-1925 does.</summary>
    public bool LogCommands { get; set; } = true;

    /// <summary>
    /// vmangos GM.LowerSecurity (mangosd.conf.dist.in:2536). Retail default is false, which lets
    /// staff act on a higher account; ArcaneCore keeps the stricter true as its default so the
    /// existing refusal does not weaken. Strong checks (mute/unmute) are strict in both.
    /// </summary>
    public bool LowerSecurity { get; set; } = true;

    /// <summary>The retail level of a stored account security (unmapped values count as Player).</summary>
    public int LevelOf(AccountSecurity security) => SecurityMap.GetValueOrDefault(security, (byte)0);

    /// <summary>Bind the section from <paramref name="configuration"/>; absent keys keep their defaults.</summary>
    public static GmOptions Bind(IConfiguration configuration)
    {
        var options = new GmOptions();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}

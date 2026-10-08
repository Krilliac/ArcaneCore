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
    /// How many of the latest audit lines <c>.arcane gmlog</c> can show (ArcaneCore only; the lines are the ones
    /// <see cref="LogCommands"/> writes, kept in memory, lost on restart). 0 keeps none.
    /// </summary>
    public int AuditTailSize { get; set; } = 200;

    /// <summary>
    /// vmangos GM.LowerSecurity (mangosd.conf.dist.in:2536). Retail default is false, which lets
    /// staff act on a higher account; ArcaneCore keeps the stricter true as its default so the
    /// existing refusal does not weaken. Strong checks (mute/unmute) are strict in both.
    /// </summary>
    public bool LowerSecurity { get; set; } = true;

    /// <summary>
    /// Treat a command above the invoker's level as if it did not exist ("There is no such
    /// command", the behaviour before the retail table work). Retail (false) resolves the command
    /// first and answers "This command is not available to you." (Chat.cpp:1884-1888).
    /// </summary>
    public bool HideUnavailable { get; set; }

    /// <summary>
    /// A command word matching a command name exactly wins over a longer name that starts with it
    /// (the behaviour before the retail table work). Retail (false) takes the first table entry the
    /// word is a prefix of (hasStringAbbr, Chat.cpp:1566-1600), with roots in retail order.
    /// </summary>
    public bool ExactNameFirst { get; set; }

    /// <summary>
    /// Apply the vmangos account level of the commands declared before the retail command work
    /// (<see cref="RetailCommandLevels"/>); off keeps their ArcaneCore four-level declarations.
    /// </summary>
    public bool RetailLevels { get; set; } = true;

    /// <summary>
    /// The most lines <c>.lookup</c> prints (0 = unlimited, as vmangos). A one-letter search on a
    /// full classic database matches about 14,000 items, each its own chat packet, all sent from
    /// the world thread; an operator may cap it (a final line says results were left out).
    /// </summary>
    public int LookupMaxResults { get; set; }

    /// <summary>
    /// ArcaneCore only (no reference core limits these): the most ticket mutations (<c>CMSG_GMTICKET_CREATE</c>,
    /// <c>_UPDATETEXT</c>, <c>_DELETETICKET</c>) one account may send per minute. Beyond it the packet is refused
    /// before anything is read (create and update answer with their error code, a delete is answered with the
    /// ticket's unchanged state) and the player is told; every accepted create or changed text tells all GameMasters
    /// online, so this also bounds that. Fail-closed: 0 refuses every ticket mutation, a negative value is the default.
    /// </summary>
    public int TicketMutationsPerMinute { get; set; } = 10;

    /// <summary>
    /// ArcaneCore only, NOT the retail default: registers the <c>.fx</c> GM tooling (music, sounds, spell visuals,
    /// cinematics, zone-under-attack, world states, client clock speed, screen messages, multi-zone weather and the
    /// <c>.fx event</c> presets), which pushes client-visible effects to the invoker, the selection, the zone, the map or
    /// the server. vmangos has no such root: only <c>.debug play music|sound|cinematic</c> and <c>.debug worldstate</c>,
    /// each to the invoker (Chat.cpp:288-323). False removes the root (restart to change).
    /// </summary>
    public bool LiveFx { get; set; } = true;

    /// <summary>
    /// ArcaneCore only: a directory of the developer's own build-5875 client DBC files (nothing is shipped). When set,
    /// <c>.fx music</c>/<c>sound</c> refuse an id not in SoundEntries.dbc, <c>.fx cinematic</c> one not in
    /// CinematicSequences.dbc and <c>.fx visual</c> one not in SpellVisualKit.dbc, and <c>.fx lookup</c> searches those
    /// files plus ZoneMusic, SpellVisualEffectName and WorldStateUI .dbc. A file missing from the directory leaves its
    /// kind unchecked (with a warning); a missing directory or a malformed file stops the daemon. Empty (the default):
    /// every id is sent unchecked. vmangos' <c>.debug play sound|music|cinematic</c> refuse unknown ids against its own
    /// loaded tables (DebugCommands.cpp:471-536); ArcaneCore loads no such tables unless this is set.
    /// </summary>
    public string LiveFxDbcDirectory { get; set; } = string.Empty;

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

using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// The vmangos account level (Common.h:136-146: MODERATOR 1, TICKETMASTER 2, GAMEMASTER 3,
/// BASIC_ADMIN 4, DEVELOPER 5, ADMINISTRATOR 6) of the commands that existed before the retail
/// command work and were declared with ArcaneCore's four-level scale, which got about half of
/// them wrong. <see cref="Apply"/> sets those levels, unless the command already carries its own
/// <see cref="ChatCommand.RetailLevel"/>. Switch off with <c>World:GmCommands:RetailLevels</c>.
/// Every entry cites its line in D:\refs\vmangos\src\game\Chat\Chat.cpp.
/// </summary>
public static class RetailCommandLevels
{
    private static readonly Dictionary<string, byte> Levels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["saveall"] = 6,             // 1261 SEC_ADMINISTRATOR
        ["announce"] = 4,            // 1234 SEC_BASIC_ADMIN
        ["notify"] = 4,              // 1235 SEC_BASIC_ADMIN
        ["gm"] = 2,                  // 392  SEC_TICKETMASTER (the "" entry of gmCommandTable)
        ["gm chat"] = 1,             // 386  SEC_MODERATOR
        ["kick"] = 2,                // 1262 SEC_TICKETMASTER
        ["tele"] = 2,                // 1215 SEC_TICKETMASTER
        ["go"] = 2,                  // 1199 SEC_TICKETMASTER
        ["go xyz"] = 2,              // 407  SEC_TICKETMASTER
        ["modify"] = 2,              // 1206 SEC_TICKETMASTER (the group; its sub-commands carry their own levels)
        ["modify money"] = 4,        // 586  SEC_BASIC_ADMIN
        ["learn"] = 5,               // 507  SEC_DEVELOPER (the "" entry)
        ["unlearn"] = 3,             // 515  SEC_GAMEMASTER (the "" entry; group 1255)
        ["cast"] = 5,                // 201  SEC_DEVELOPER (the "" entry)
        ["unaura"] = 3,              // 1233 SEC_GAMEMASTER
        ["cooldown"] = 3,            // 1254 SEC_GAMEMASTER (list/clear 283-284)
        ["instance"] = 2,            // 1202 SEC_TICKETMASTER (the group)
        ["instance listbinds"] = 3,  // 482  SEC_GAMEMASTER
        ["instance unbind"] = 3,     // 483  SEC_GAMEMASTER
        ["instance stats"] = 4,      // 485  SEC_BASIC_ADMIN
        ["guild"] = 3,               // 1201 SEC_GAMEMASTER (the group)
        ["guild create"] = 3,        // 448  SEC_GAMEMASTER
        ["guild delete"] = 4,        // 449  SEC_BASIC_ADMIN
        ["guild invite"] = 3,        // 450  SEC_GAMEMASTER
        ["guild uninvite"] = 3,      // 451  SEC_GAMEMASTER
        ["guild rank"] = 3,          // 452  SEC_GAMEMASTER
    };

    /// <summary>The retail level recorded for the command at <paramref name="path"/> (lower-case words joined by spaces), if any.</summary>
    public static byte? Of(string path) => Levels.TryGetValue(path, out byte level) ? level : null;

    /// <summary>A copy of <paramref name="commands"/> with the retail levels set on every command this table knows.</summary>
    public static List<ChatCommand> Apply(IEnumerable<ChatCommand> commands, string prefix = "")
        => [.. commands.Select(command =>
        {
            string path = prefix.Length == 0 ? command.Name : prefix + " " + command.Name;
            ChatCommand updated = command.RetailLevel is null && Of(path) is { } level ? command with { RetailLevel = level } : command;
            return command.SubCommands.Count == 0 ? updated : updated with { Children = Apply(command.SubCommands, path) };
        })];
}

using ArcaneCore.Game.Guilds;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Social;

/// <summary>
/// <c>.guild create/invite/uninvite/rank/delete</c> (cmangos/vmangos Level2/Level3.cpp
/// HandleGuild*Command, SEC_GAMEMASTER). A character name may be omitted to use the selected
/// player or yourself; guild names are quoted.
/// </summary>
public sealed class GuildCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("guild", AccountSecurity.GameMaster, "Guild administration.", Children:
        [
            new ChatCommand("create", AccountSecurity.GameMaster, "Syntax: .guild create [$GuildLeaderName] \"$GuildName\" — found a guild led by the character.", Create),
            new ChatCommand("invite", AccountSecurity.GameMaster, "Syntax: .guild invite [$CharacterName] \"$GuildName\" — add the character to the guild.", Invite),
            new ChatCommand("uninvite", AccountSecurity.GameMaster, "Syntax: .guild uninvite [$CharacterName] — remove the character from its guild.", Uninvite),
            new ChatCommand("rank", AccountSecurity.GameMaster, "Syntax: .guild rank [$CharacterName] #Rank — set the character's guild rank (0 is guild master).", Rank),
            new ChatCommand("delete", AccountSecurity.GameMaster, "Syntax: .guild delete \"$GuildName\" — disband the guild.", Delete),
        ]),
    ];

    private static GuildManager Guilds(CommandContext context) => SocialHandlers.Social(context.Session).Guilds;

    private static bool Create(CommandContext context, string args)
    {
        if (!TrySplitQuoted(args, out string name, out string guildName))
        {
            return false;
        }

        if (ResolveCharacter(context, name) is not { } id)
        {
            return true;
        }

        GuildAdminResult result = Guilds(context).Create(id, guildName, out _);
        context.Reply(result == GuildAdminResult.Ok ? $"Guild {guildName} created." : Describe(result));
        return true;
    }

    private static bool Invite(CommandContext context, string args)
    {
        if (!TrySplitQuoted(args, out string name, out string guildName))
        {
            return false;
        }

        if (ResolveCharacter(context, name) is not { } id)
        {
            return true;
        }

        GuildAdminResult result = Guilds(context).AdminInvite(id, guildName);
        context.Reply(result == GuildAdminResult.Ok ? $"Added to {guildName}." : Describe(result));
        return true;
    }

    private static bool Uninvite(CommandContext context, string args)
    {
        if (ResolveCharacter(context, args.Trim()) is not { } id)
        {
            return true;
        }

        GuildAdminResult result = Guilds(context).AdminUninvite(id);
        context.Reply(result == GuildAdminResult.Ok ? "Removed from the guild." : Describe(result));
        return true;
    }

    private static bool Rank(CommandContext context, string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || !byte.TryParse(parts[^1], out byte rank))
        {
            return false;
        }

        if (ResolveCharacter(context, parts.Length == 2 ? parts[0] : string.Empty) is not { } id)
        {
            return true;
        }

        GuildAdminResult result = Guilds(context).AdminSetRank(id, rank);
        context.Reply(result == GuildAdminResult.Ok ? $"Rank set to {rank}." : Describe(result));
        return true;
    }

    private static bool Delete(CommandContext context, string args)
    {
        if (!TrySplitQuoted(args, out string rest, out string guildName) || rest.Length != 0)
        {
            return false;
        }

        GuildAdminResult result = Guilds(context).Delete(guildName);
        context.Reply(result == GuildAdminResult.Ok ? $"Guild {guildName} deleted." : Describe(result));
        return true;
    }

    /// <summary>Split <c>[name] "quoted text"</c>; false when no quoted part exists.</summary>
    internal static bool TrySplitQuoted(string args, out string before, out string quoted)
    {
        before = string.Empty;
        quoted = string.Empty;
        int open = args.IndexOf('"', StringComparison.Ordinal);
        int close = open < 0 ? -1 : args.IndexOf('"', open + 1);
        if (open < 0 || close < 0)
        {
            return false;
        }

        before = args[..open].Trim();
        quoted = args[(open + 1)..close].Trim();
        return quoted.Length > 0 && args[(close + 1)..].Trim().Length == 0;
    }

    /// <summary>The named character (online or not), or the selected player / yourself when no name is given.</summary>
    private static uint? ResolveCharacter(CommandContext context, string name)
    {
        if (name.Length == 0)
        {
            if (context.SelectedPlayerOrSelf() is { } selected)
            {
                return selected.Guid.Low;
            }

            context.Reply("No player selected.");
            return null;
        }

        if (SocialHandlers.Social(context.Session).Characters.FindByName(CharacterNames.Normalize(name)) is { } info)
        {
            return info.Id;
        }

        context.Reply($"Player {name} not found.");
        return null;
    }

    private static string Describe(GuildAdminResult result) => result switch
    {
        GuildAdminResult.NameInvalid => "Invalid guild name.",
        GuildAdminResult.NameExists => "A guild with that name already exists.",
        GuildAdminResult.AlreadyInGuild => "The player is already in a guild.",
        GuildAdminResult.GuildNotFound => "No guild with that name.",
        GuildAdminResult.NotInGuild => "The player is not in a guild.",
        GuildAdminResult.RankInvalid => "That rank does not exist in the guild.",
        GuildAdminResult.CharacterNotFound => "Player not found.",
        GuildAdminResult.NotLoaded => "Guilds are not loaded yet.",
        _ => "Failed.",
    };
}

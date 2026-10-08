using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Social;

/// <summary>
/// <c>.guild create/invite/uninvite/rank/delete</c> (cmangos/vmangos Level2/Level3.cpp
/// HandleGuild*Command, SEC_GAMEMASTER). A character name may be omitted to use the selected
/// player or yourself; guild names are quoted. The character's account must not outrank the invoker
/// (<see cref="CommandContext.CanActOn"/>; for an offline character the owner account is read first, so the
/// answer comes a moment later). <c>.guild delete</c> removes every member, so it applies the check to each
/// member and refuses when any of them outranks the invoker. vmangos has no such check on these commands;
/// ArcaneCore applies it to every GM command that changes another player (docs/integration/gm-commands.md).
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

        ActOnCharacter(context, name, id =>
        {
            GuildAdminResult result = Guilds(context).Create(id, guildName, out _);
            context.Reply(result == GuildAdminResult.Ok ? $"Guild {guildName} created." : Describe(result));
        });
        return true;
    }

    private static bool Invite(CommandContext context, string args)
    {
        if (!TrySplitQuoted(args, out string name, out string guildName))
        {
            return false;
        }

        ActOnCharacter(context, name, id =>
        {
            GuildAdminResult result = Guilds(context).AdminInvite(id, guildName);
            context.Reply(result == GuildAdminResult.Ok ? $"Added to {guildName}." : Describe(result));
        });
        return true;
    }

    private static bool Uninvite(CommandContext context, string args)
    {
        ActOnCharacter(context, args.Trim(), id =>
        {
            GuildAdminResult result = Guilds(context).AdminUninvite(id);
            context.Reply(result == GuildAdminResult.Ok ? "Removed from the guild." : Describe(result));
        });
        return true;
    }

    private static bool Rank(CommandContext context, string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || !byte.TryParse(parts[^1], out byte rank))
        {
            return false;
        }

        ActOnCharacter(context, parts.Length == 2 ? parts[0] : string.Empty, id =>
        {
            GuildAdminResult result = Guilds(context).AdminSetRank(id, rank);
            context.Reply(result == GuildAdminResult.Ok ? $"Rank set to {rank}." : Describe(result));
        });
        return true;
    }

    private static bool Delete(CommandContext context, string args)
    {
        if (!TrySplitQuoted(args, out string rest, out string guildName) || rest.Length != 0)
        {
            return false;
        }

        GuildManager guilds = Guilds(context);
        if (guilds.GetByName(guildName) is not { } guild)
        {
            context.Reply(Describe(GuildAdminResult.GuildNotFound));
            return true;
        }

        // Disbanding removes every member, so the invoker must be able to act on each of them: an online member by
        // its session's security, an offline one by its owner account's (read off the world thread).
        SocialContext social = SocialHandlers.Social(context.Session);
        uint[] checkedMembers = [.. guild.Members.Select(m => m.CharacterId)];
        var offlineAccounts = new List<int>();
        foreach (uint memberId in checkedMembers)
        {
            if (social.Characters.Find(memberId) is not { } info)
            {
                continue; // no character, no account to protect
            }

            if (context.World.FindOnlinePlayer(info.Name) is { } online)
            {
                if (!context.CanActOn(online))
                {
                    return true;
                }
            }
            else
            {
                offlineAccounts.Add(info.AccountId);
            }
        }

        GmTargets.ActOnOffline(context, offlineAccounts, () =>
        {
            if (!ReferenceEquals(guilds.GetByName(guildName), guild))
            {
                context.Reply(Describe(GuildAdminResult.GuildNotFound));
            }
            else if (guild.Members.Any(m => !checkedMembers.Contains(m.CharacterId)))
            {
                // Someone joined while the offline members were being checked; they have not been.
                context.Reply("The guild's members changed; nothing was changed. Try again.");
            }
            else
            {
                GuildAdminResult result = guilds.Delete(guildName);
                context.Reply(result == GuildAdminResult.Ok ? $"Guild {guildName} deleted." : Describe(result));
            }
        }, unknownAccountIsNotFound: false);
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

    /// <summary>
    /// Run <paramref name="act"/> with the id of the named character (online or not), or of the selected player /
    /// yourself when no name is given, once the target-rank check allows it; otherwise reply why not.
    /// </summary>
    private static void ActOnCharacter(CommandContext context, string name, Action<uint> act)
    {
        if (name.Length == 0)
        {
            if (context.SelectedPlayerOrSelf() is not { } selected)
            {
                context.Reply("No player selected.");
            }
            else if (context.CanActOn(selected))
            {
                act(selected.Guid.Low);
            }

            return;
        }

        if (SocialHandlers.Social(context.Session).Characters.FindByName(CharacterNames.Normalize(name)) is not { } info)
        {
            context.Reply($"Player {name} not found.");
        }
        else if (context.World.FindOnlinePlayer(info.Name) is { } online)
        {
            if (context.CanActOn(online))
            {
                act(info.Id);
            }
        }
        else
        {
            GmTargets.ActOnOffline(context, info.AccountId, () => act(info.Id));
        }
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

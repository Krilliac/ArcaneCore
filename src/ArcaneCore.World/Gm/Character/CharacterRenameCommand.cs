using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// <c>.character rename [$name]</c> (mangos HandleCharacterRenameCommand, PlayerCommands.cpp:166-200; SEC_GAMEMASTER, Chat.cpp:237), added under
/// the <c>.character</c> root another feature defines (<c>.character reputation</c>): flags the selected character, or the one named,
/// online or not, so the player is asked to choose a new name (<see cref="CharacterAtLoginFlags.Rename"/>, answered by CMSG_CHAR_RENAME).
/// The level is 3 on the vmangos scale, like the other GM-level character commands; vmangos' own value for this command is UNVERIFIED
/// (the vmangos source is not available here; mangos zero says SEC_GAMEMASTER).
/// <para>
/// The target's account must not outrank the invoker (vmangos HasLowerSecurity: online through the shared check, offline through the
/// account that owns the character, read from the account store). The flag is written to the characters database off the world thread;
/// a failure is reported to the invoker. The other <c>.character</c> sub-commands (<c>level</c>, <c>erase</c>, <c>reputation</c>,
/// <c>deleted</c>) are not provided here.
/// </para>
/// </summary>
public sealed class CharacterRenameExtension : ICommandExtension
{
    private const string RenamePlayer = "Forced rename for player {0} will be requested at next login."; // LANG_RENAME_PLAYER (Language.h:222)
    private const string RenamePlayerGuid = "Forced rename for player {0} (GUID #{1}) will be requested at next login."; // LANG_RENAME_PLAYER_GUID (:223)
    private const string DatabaseError = "The character database is unavailable; the rename request was not stored. See the server log.";

    public string Path => "character";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("rename", AccountSecurity.GameMaster,
            "Syntax: .character rename [$name]\nRequest a rename of the selected character, or the named one (online or not); the player is asked for a new name at the character screen.",
            Rename, RetailLevel: 3),
    ];

    private static bool Rename(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            Player? selected = context.SelectedPlayerOrSelf();
            if (selected is null)
            {
                context.Reply(GmStrings.NoCharSelected);
                return true;
            }

            return RenameOnline(context, selected);
        }

        string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
        if (raw is null || !PlayerNames.TryNormalize(raw, out string name))
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        return context.World.FindOnlinePlayer(name) is { } online ? RenameOnline(context, online) : RenameOffline(context, name);
    }

    private static bool RenameOnline(CommandContext context, Player target)
    {
        if (!context.CanActOn(target))
        {
            return true;
        }

        context.Reply(string.Format(RenamePlayer, GmStrings.PlayerLink(target.Name)));
        int id = (int)target.Guid.Low;
        Run(context, async services =>
        {
            if (!await services.GetRequiredService<ICharacterRenameStore>().SetFlagAsync(id, CharacterAtLoginFlags.Rename).ConfigureAwait(false))
            {
                context.Reply(GmStrings.PlayerNotFound); // deleted meanwhile
            }
        });
        return true;
    }

    private static bool RenameOffline(CommandContext context, string name)
    {
        CharacterIdentity? identity = context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(name);
        if (identity is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        Run(context, async services =>
        {
            // The owner's security decides, as HasLowerSecurity(NULL, guid) does through the guid's account.
            IReadOnlyDictionary<int, string> names = await services.GetRequiredService<IAccountAdmin>()
                .GetUsernamesAsync([identity.AccountId]).ConfigureAwait(false);
            Account? owner = names.TryGetValue(identity.AccountId, out string? username)
                ? await services.GetRequiredService<IAccountStore>().FindByUsernameAsync(username).ConfigureAwait(false)
                : null;
            if (owner is null)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return;
            }

            if (GmSecurity.HasLowerSecurity(context.Security, owner.Security, strong: false, context.Commands.Gm))
            {
                context.Reply(GmStrings.SecurityTooLow);
                return;
            }

            context.Reply(string.Format(RenamePlayerGuid, GmStrings.PlayerLink(identity.Name), identity.Id));
            if (!await services.GetRequiredService<ICharacterRenameStore>().SetFlagAsync(identity.Id, CharacterAtLoginFlags.Rename).ConfigureAwait(false))
            {
                context.Reply(GmStrings.PlayerNotFound);
            }
        });
        return true;
    }

    /// <summary>Run database work off the world thread in its own scope; a failure is logged and reported to the invoker.</summary>
    private static void Run(CommandContext context, Func<IServiceProvider, Task> work)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "the .character rename command failed");
                context.Reply(DatabaseError);
            }
        });
    }
}

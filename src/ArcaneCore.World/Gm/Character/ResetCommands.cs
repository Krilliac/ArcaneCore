using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// The <c>.reset</c> root (vmangos resetCommandTable, Chat.cpp:913-923; the root is SEC_GAMEMASTER, :1213) with its talent
/// sub-commands: <c>.reset talents [$name]</c> (SEC_GAMEMASTER, :919; HandleResetTalentsCommand, CharacterCommands.cpp:3892-3915)
/// and <c>.reset all talents</c> (SEC_ADMINISTRATOR, :921; HandleResetAllCommand, :3969-3986). <c>honor</c>, <c>level</c>,
/// <c>spells</c>, <c>stats</c> and <c>items</c> are not provided.
/// <para>
/// Differences (docs/areas/talents.md): the reset-at-login request is kept in <c>character_at_login</c>, not in
/// <c>characters.character_flags</c>; <c>.reset all</c> requires the word <c>talents</c> (vmangos resets talents whatever the
/// argument); a request that cannot be stored is reported to the invoker. Without the talent system (no Talent.dbc) both commands
/// say so and change nothing.
/// </para>
/// </summary>
public sealed class ResetCommands : ICommandGroup
{
    /// <summary>LANG_RESET_TALENTS_ONLINE (mangos_string 213).</summary>
    public const string ResetOnline = "Talents of {0} reset.";

    /// <summary>LANG_RESET_TALENTS_OFFLINE (mangos_string 214).</summary>
    public const string ResetOffline = "Talents of {0} will reset at next login.";

    /// <summary>LANG_RESETALL_TALENTS (mangos_string 219), sent to every player (World::SendWorldText).</summary>
    public const string ResetAllText = "Talents will reset for all players at login. Strongly recommend re-login!";

    public const string TalentsInert = "The talent system is not enabled on this server (Talents:TalentDbcPath is unset).";

    public const string StoreFailed = "The character database is unavailable; the talent reset request was not stored. See the server log.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("reset", AccountSecurity.GameMaster, "Syntax: .reset $subcommand\nType .reset to see the list of possible subcommands or .help reset $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("talents", AccountSecurity.GameMaster,
                "Syntax: .reset talents [$playername]\nRemove all talents of the selected player or the named one (online now, or at the next login when offline), for free.",
                Talents, RetailLevel: 3),
            new ChatCommand("all", AccountSecurity.Administrator,
                "Syntax: .reset all talents\nRequest a free talent reset of every character at its next login.",
                All, RetailLevel: 6),
        ], RetailLevel: 3),
    ];

    private static TalentFeature? Feature(CommandContext context)
        => context.Session.Services.GetService<TalentFeature>() is { Service: not null } feature ? feature : null;

    private static bool Talents(CommandContext context, string text)
    {
        if (Feature(context) is not { } feature)
        {
            context.Reply(TalentsInert);
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            Player? selected = context.SelectedPlayerOrSelf();
            if (selected is null)
            {
                context.Reply(GmStrings.PlayerNotFound);
                return true;
            }

            return ResetOnlineTarget(context, feature, selected);
        }

        string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
        if (raw is null || !PlayerNames.TryNormalize(raw, out string name))
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        return context.World.FindOnlinePlayer(name) is { } online
            ? ResetOnlineTarget(context, feature, online)
            : FlagOffline(context, feature, name);
    }

    private static bool ResetOnlineTarget(CommandContext context, TalentFeature feature, Player target)
    {
        if (!context.CanActOn(target))
        {
            return true;
        }

        feature.ResetTalentsNow(target);
        target.SendSystemMessage(TalentFeature.TalentsResetText);
        if (!ReferenceEquals(target, context.Player))
        {
            context.Reply(string.Format(ResetOnline, GmStrings.PlayerLink(target.Name)));
        }

        return true;
    }

    private static bool FlagOffline(CommandContext context, TalentFeature feature, string name)
    {
        CharacterIdentity? identity = context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(name);
        if (identity is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        // The owner's security decides, as ExtractPlayerTarget -> HasLowerSecurity(NULL, guid) does through the guid's account.
        GmTargets.ActOnOffline(context, identity.AccountId, () => Store(context, feature,
            store => store.FlagAsync(identity.Id),
            flagged => context.Reply(flagged ? string.Format(ResetOffline, GmStrings.PlayerLink(identity.Name)) : GmStrings.PlayerNotFound)));
        return true;
    }

    private static bool All(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.ExtractArg() is not { } what || !what.Equals("talents", StringComparison.OrdinalIgnoreCase) || !args.IsEmpty)
        {
            return false;
        }

        if (Feature(context) is not { } feature)
        {
            context.Reply(TalentsInert);
            return true;
        }

        // vmangos: the world text first, then every stored character and every online player is flagged.
        context.World.BroadcastToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(ResetAllText));
        feature.MarkOnlineForLoginReset(context.World.OnlinePlayers);
        Store(context, feature, store => store.FlagAllAsync(), count =>
            context.Session.Logger.LogInformation("{Gm} requested a talent reset of every character ({Count} newly flagged)", context.Player.Name, count));
        return true;
    }

    /// <summary>Run <paramref name="work"/> on the request store off the world thread; <paramref name="done"/> runs on the world thread.</summary>
    private static void Store<T>(CommandContext context, TalentFeature feature, Func<ITalentResetFlagStore, Task<T>> work, Action<T> done)
    {
        ILogger logger = context.Session.Logger;
        _ = Task.Run(async () =>
        {
            T result;
            try
            {
                result = await feature.WithResetFlagsAsync(work).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogError(ex, "storing a talent reset request failed");
                context.World.Post(() => context.Reply(StoreFailed));
                return;
            }

            context.World.Post(() => done(result));
        });
    }
}

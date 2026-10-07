using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// <c>.pinfo</c>, <c>.mute</c>, <c>.unmute</c>, <c>.gmannounce</c> and <c>.gmnotify</c> (docs/integration/gm-audit-lane.md has the
/// reference-versus-ours table). Levels are ArcaneCore's: pinfo needs GameMaster, mute/unmute and the staff
/// announcements Moderator. Mute is on the ACCOUNT (vmangos <c>account.mutetime</c>) and persists through
/// <see cref="GmAuditFeature"/>; the chat gates read it as an <see cref="IChatMuteSource"/>. Muting needs an online target
/// of strictly lower security (vmangos HasLowerSecurity with strong = true, CommunicationCommands.cpp:88-140); unmuting also
/// works on an offline character's account, where the muting staff member's stored security stands in for the target's.
/// A mute set by higher staff is never shortened or lifted by lower staff, the muted staff member included.
/// <c>.unmute</c> also clears the chat lane's flood mute (vmangos clears <c>m_muteTime</c> outright).
/// </summary>
public sealed class AuditCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("pinfo", AccountSecurity.GameMaster, "Syntax: .pinfo [$playername]\nShow account, level, position, chat-mute and ticket state of the named character (online or not), the selected player, or yourself.", Pinfo),
        new ChatCommand("mute", AccountSecurity.Moderator, "Syntax: .mute [$playername] $duration [$reason]\nDisable the chat of the player's account. $duration is a number of minutes or like 1d2h30m (1 second to 365 days). The player must be online.", Mute),
        new ChatCommand("unmute", AccountSecurity.Moderator, "Syntax: .unmute [$playername]\nEnable the chat of the player's account again. The character may be offline.", Unmute),
        new ChatCommand("gmannounce", AccountSecurity.Moderator, "Syntax: .gmannounce $message\nSend a chat message to every staff member online.", GmAnnounce),
        new ChatCommand("gmnotify", AccountSecurity.Moderator, "Syntax: .gmnotify $message\nSend an on-screen notification to every staff member online.", GmNotify),
    ];

    // ---- .pinfo ---------------------------------------------------------------------------------

    private static bool Pinfo(CommandContext context, string text)
    {
        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        var args = new CommandArgs(text);
        Player? player;
        CharacterIdentity? identity = null;
        if (args.IsEmpty)
        {
            player = context.SelectedPlayerOrSelf();
        }
        else
        {
            string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
            if (raw is null || !PlayerNames.TryNormalize(raw, out string name))
            {
                context.Reply(GmStrings.PlayerNotFound);
                return true;
            }

            player = context.World.FindOnlinePlayer(name);
            identity = player is null ? context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(name) : null;
        }

        if (player is null && identity is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        // vmangos HandlePInfoCommand: "check online security" (HasLowerSecurity) before anything about the target is shown.
        if (player is not null && !context.CanActOn(player))
        {
            return true;
        }

        int characterId;
        int accountId;
        if (player is not null)
        {
            characterId = (int)player.Guid.Low;
            accountId = player.AccountId;
            context.Reply(GmAuditStrings.PinfoOnline(GmStrings.PlayerLink(player.Name), (uint)characterId, accountId, player.Security));
            context.Reply(GmAuditStrings.PinfoLevel(player.Level, GmAuditStrings.Span(player.PlayedTimeAt(context.World.NowMs)), player.Money));
            context.Reply(GmAuditStrings.PinfoPosition(player.MapId, player.ZoneId, player.X, player.Y, player.Z));
        }
        else
        {
            characterId = identity!.Id;
            accountId = identity.AccountId;
            context.Reply(GmAuditStrings.PinfoOffline(GmStrings.PlayerLink(identity.Name), (uint)characterId, accountId));
            context.Reply(GmAuditStrings.PinfoOfflineLevel(identity.Level, identity.ZoneId));
        }

        GmTicketRecord? ticket = audit.OpenTicketOf(characterId);
        string muteState = GmAuditStrings.MuteState(audit.MuteOf(accountId), audit.NowUnixSeconds);
        if (player is not null && audit.MuteOf(accountId) is null)
        {
            // No account mute, but the chat lane's flood mute may still hold the player: say so rather than "enabled".
            ChatFeature chat = context.Session.Services.GetRequiredService<ChatFeature>();
            long floodUntil = chat.MutedUntil(player);
            if (floodUntil > chat.NowUnixSeconds)
            {
                muteState = GmAuditStrings.FloodMuteState(GmAuditStrings.Span(floodUntil - chat.NowUnixSeconds));
            }
        }

        context.Reply(GmAuditStrings.PinfoState(player?.IsGameMaster, muteState, ticket?.Id));
        return true;
    }

    // ---- .mute / .unmute ------------------------------------------------------------------------

    private static bool Mute(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? nameArg = null;
        if (!args.IsEmpty && !GmMuteDuration.LooksLikeDuration(FirstWord(text)))
        {
            nameArg = args.ExtractKeyFromLink("Hplayer", out _, out _);
            if (nameArg is null)
            {
                return false;
            }
        }

        string? durationText = args.ExtractLiteral();
        if (durationText is null || !GmMuteDuration.TryParse(durationText, out long seconds))
        {
            return false;
        }

        string reason = CleanReason(args.Rest);
        if (!GmTargets.TryPlayer(context, nameArg, out Player target) || !context.CanActOn(target, strong: true))
        {
            return true;
        }

        // The mute's AUTHOR decides, whoever the target is: CanActOn lets staff target themselves, and a muted staff member
        // can still reach the command table (a whisper is not gated by the mute), so a self-target must not skip this.
        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        if (audit.MuteOf(target.AccountId) is { } existing && existing.MutedBySecurity > (byte)context.Security)
        {
            context.Reply(GmAuditStrings.MuteSetByHigher);
            return true;
        }

        audit.Mute(target.AccountId, seconds, context.Player.Name, context.Security, reason);
        string duration = GmAuditStrings.Span(seconds);
        target.SendSystemMessage(GmAuditStrings.YourChatDisabled(duration, context.Player.Name, reason));
        context.Reply(GmAuditStrings.YouDisableChat(GmStrings.PlayerLink(target.Name), duration));
        return true;
    }

    private static bool Unmute(CommandContext context, string text)
    {
        GmAuditFeature audit = context.Session.Services.GetRequiredService<GmAuditFeature>();
        var args = new CommandArgs(text);
        Player? target;
        CharacterIdentity? offline = null;
        string name;
        if (args.IsEmpty)
        {
            target = context.SelectedPlayerOrSelf();
            name = target?.Name ?? string.Empty;
        }
        else
        {
            string? raw = args.ExtractKeyFromLink("Hplayer", out _, out _);
            if (raw is null || !PlayerNames.TryNormalize(raw, out name))
            {
                context.Reply(GmStrings.PlayerNotFound);
                return true;
            }

            target = context.World.FindOnlinePlayer(name);
            offline = target is null ? context.Session.Services.GetRequiredService<CharacterDirectory>().FindByName(name) : null;
        }

        if (target is null && offline is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (target is not null && !context.CanActOn(target, strong: true))
        {
            return true;
        }

        int accountId = target?.AccountId ?? offline!.AccountId;
        AccountMuteRecord? mute = audit.MuteOf(accountId);

        // Checked against the mute's author for every target, the invoker included (see Mute).
        if (mute is not null && mute.MutedBySecurity > (byte)context.Security)
        {
            context.Reply(GmStrings.SecurityTooLow);
            return true;
        }

        // vmangos .unmute clears m_muteTime entirely, so the chat lane's own flood mute goes too (it is keyed by account,
        // so an offline character's account is covered); "already enabled" is only said when neither mute was in force.
        bool floodMuteCleared = context.Session.Services.GetRequiredService<ChatFeature>().ClearMute(accountId);
        if (mute is null && !floodMuteCleared)
        {
            context.Reply(GmAuditStrings.ChatAlreadyEnabled);
            return true;
        }

        if (mute is not null)
        {
            audit.Unmute(accountId);
        }

        target?.SendSystemMessage(GmAuditStrings.YourChatEnabled);
        context.Reply(GmAuditStrings.YouEnableChat(GmStrings.PlayerLink(target?.Name ?? offline!.Name)));
        return true;
    }

    /// <summary>A reason on one line, at most 255 characters (the column), control characters turned into spaces.</summary>
    public static string CleanReason(string reason)
    {
        string clean = new([.. reason.Trim().Select(c => char.IsControl(c) ? ' ' : c)]);
        if (clean.Length > 255)
        {
            clean = clean[..255];
        }

        return clean.Length == 0 ? GmAuditStrings.NoReason : clean;
    }

    private static string FirstWord(string text)
    {
        string trimmed = text.TrimStart();
        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? trimmed : trimmed[..space];
    }

    // ---- .gmannounce / .gmnotify ----------------------------------------------------------------

    private static bool GmAnnounce(CommandContext context, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        byte[] packet = ChatPackets.BuildSystemMessage(GmAuditStrings.GmAnnouncement(context.Player.Name, text));
        foreach (Player staff in context.World.OnlinePlayers.Where(p => p.Security > AccountSecurity.Player))
        {
            staff.Session.Send(WorldOpcode.SmsgMessagechat, packet);
        }

        return true;
    }

    private static bool GmNotify(CommandContext context, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        byte[] packet = ChatPackets.BuildNotification(GmAuditStrings.GmNotifyPrefix + text);
        foreach (Player staff in context.World.OnlinePlayers.Where(p => p.Security > AccountSecurity.Player))
        {
            staff.Session.Send(WorldOpcode.SmsgNotification, packet);
        }

        return true;
    }
}

using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// <c>.arcane stream on|off|list</c> (GameMaster): turn a named <see cref="GmStream"/> channel on or off for the invoking GM, or list
/// the channels followed. A subscription times out by itself (<see cref="GmStreamOptions.AutoOffAfter"/>) and ends at logout; running
/// <c>on</c> again renews it. The channel names are defined by the features that publish to the stream. Changes no world state, only
/// this GM's own subscriptions, and needs a player (the console has none).
/// </summary>
public sealed class GmStreamCommands : ICommandExtension
{
    /// <summary>The longest channel name accepted.</summary>
    public const int MaxChannelLength = 24;

    public string Path => "arcane";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("stream", AccountSecurity.GameMaster,
            "Syntax: .arcane stream [on|off|list]\nFollow a live debug channel in chat. Each subscription turns itself off after a few minutes and when you log out.", List, Children:
            [
                new ChatCommand("on", AccountSecurity.GameMaster, "Syntax: .arcane stream on $channel\nFollow $channel (letters, digits, '-' and '_'); running it again renews the timeout.", On),
                new ChatCommand("off", AccountSecurity.GameMaster, "Syntax: .arcane stream off $channel|all\nStop following $channel, or every channel.", Off),
                new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .arcane stream list\nShow the channels you follow and when each turns off.", List),
            ]),
    ];

    /// <summary>Whether <paramref name="channel"/> is a legal channel name.</summary>
    public static bool IsValidChannel(string channel)
        => channel.Length is > 0 and <= MaxChannelLength && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static GmStream? StreamOf(CommandContext context)
    {
        GmStream? stream = context.Session.Services.GetService<GmStreamFeature>()?.Stream;
        if (stream is null)
        {
            context.Reply("The GM stream is not available.");
        }

        return stream;
    }

    private static bool On(CommandContext context, string args)
    {
        string channel = args.Trim();
        if (!IsValidChannel(channel))
        {
            return false;
        }

        if (StreamOf(context) is not { } stream)
        {
            return true;
        }

        if (!stream.Subscribe(context.Player.Guid, channel))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"You already follow {stream.Options.MaxChannelsPerGm} channels; turn one off first."));
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Following [{channel}]; it turns off after {stream.Options.AutoOffAfter.TotalMinutes:0.##} min or when you log out."));
        return true;
    }

    private static bool Off(CommandContext context, string args)
    {
        string channel = args.Trim();
        if (channel.Length == 0 || (!channel.Equals("all", StringComparison.OrdinalIgnoreCase) && !IsValidChannel(channel)))
        {
            return false;
        }

        if (StreamOf(context) is not { } stream)
        {
            return true;
        }

        ObjectGuid gm = context.Player.Guid;
        if (channel.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Stopped {stream.UnsubscribeAll(gm)} channel(s)."));
        }
        else
        {
            context.Reply(stream.Unsubscribe(gm, channel) ? $"Stopped following [{channel}]." : $"You do not follow [{channel}].");
        }

        return true;
    }

    private static bool List(CommandContext context, string args)
    {
        if (args.Trim().Length > 0 && !args.Trim().Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (StreamOf(context) is not { } stream)
        {
            return true;
        }

        IReadOnlyList<(string Channel, DateTimeOffset ExpiresAt)> subscriptions = stream.Subscriptions(context.Player.Guid);
        if (subscriptions.Count == 0)
        {
            context.Reply("You follow no stream channels.");
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach ((string channel, DateTimeOffset expiresAt) in subscriptions)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"[{channel}] turns off in {Math.Max(0, (int)(expiresAt - now).TotalSeconds)} s"));
        }

        return true;
    }
}

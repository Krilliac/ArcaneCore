using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Diagnostics;

/// <summary>
/// <c>.arcane stream on|off|list</c> (GameMaster): turn a named <see cref="GmStream"/> channel on or off for the invoking GM, or list
/// the channels followed and the channels that exist. A subscription takes a scope (<c>target</c>, the default: what involves you or
/// your selected unit nearby; <c>near</c>: everything nearby; <c>all</c>: everything, not for every channel), times out by itself
/// (<see cref="GmStreamOptions.AutoOffAfter"/>) and ends at logout; running <c>on</c> again renews it and may change the scope. The channels
/// are <see cref="GmStreamChannels.All"/>: <c>damage</c>, <c>procs</c>, <c>spells</c>, <c>moves</c> and <c>anticheat</c>. Changes no
/// world state, only this GM's own subscriptions, and needs a player (the console has none).
/// </summary>
public sealed class GmStreamCommands : ICommandExtension
{
    /// <summary>The longest channel name accepted.</summary>
    public const int MaxChannelLength = 24;

    public string Path => "arcane";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("stream", AccountSecurity.GameMaster,
            "Syntax: .arcane stream [on|off|list]\nFollow a live debug channel in chat (damage, procs, spells, moves, anticheat). With no argument it lists what you follow and the available channels. Each subscription turns itself off after a few minutes and when you log out.", List, Children:
            [
                new ChatCommand("on", AccountSecurity.GameMaster, "Syntax: .arcane stream on $channel [target|near|all]\nFollow $channel: damage, procs, spells, moves or anticheat (anticheat: findings and packet-heuristics reports with the score and verdict). Scope target (default) shows events of you or your selected unit nearby, near shows every event nearby, all shows every event (not for moves). Running it again renews the timeout and sets the scope.", On, IsWrite: true),
                new ChatCommand("off", AccountSecurity.GameMaster, "Syntax: .arcane stream off $channel|all\nStop following $channel, or every channel.", Off, IsWrite: true),
                new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .arcane stream list\nShow the channels you follow with scope and when each turns off, then the channels available.", List),
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

    private static DateTimeOffset? ExpiryOf(GmStream stream, ObjectGuid gm, string channel)
    {
        foreach (GmStreamSubscription followed in stream.Details(gm))
        {
            if (followed.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase))
            {
                return followed.ExpiresAt;
            }
        }

        return null;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

    private static string ScopeText(GmStreamScope scope) => scope.ToString().ToLowerInvariant();

    private static bool TryParseScope(string text, out GmStreamScope scope)
    {
        switch (text.ToLowerInvariant())
        {
            case "target":
                scope = GmStreamScope.Target;
                return true;
            case "near":
                scope = GmStreamScope.Near;
                return true;
            case "all":
                scope = GmStreamScope.All;
                return true;
            default:
                scope = GmStreamScope.Target;
                return false;
        }
    }

    private static bool On(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is 0 or > 2 || !IsValidChannel(words[0]))
        {
            return false;
        }

        GmStreamScope scope = GmStreamScope.Target;
        if (words.Length == 2 && !TryParseScope(words[1], out scope))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Unknown scope '{words[1]}'; use target, near or all."));
            return true;
        }

        if (GmStreamChannels.Find(words[0]) is not { } info)
        {
            string available = string.Join(", ", GmStreamChannels.All.Select(c => c.Name));
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Unknown channel '{words[0]}'. Available: {available}."));
            return true;
        }

        if (scope == GmStreamScope.All && !info.AllowsAll)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"[{info.Name}] cannot be followed for everyone (all); use target or near."));
            return true;
        }

        string channel = info.Name;
        if (StreamOf(context) is not { } stream)
        {
            return true;
        }

        ObjectGuid self = context.Player.Guid;
        DateTimeOffset? before = ExpiryOf(stream, self, channel);
        if (!stream.Subscribe(self, channel, scope))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"You already follow {stream.Options.MaxChannelsPerGm} channels; turn one off first."));
            return true;
        }

        string reason = "scope " + ScopeText(scope);
        if (before is { } previous && ExpiryOf(stream, self, channel) is { } renewed)
        {
            DebugWrite.Record(context, "gm stream " + channel, "expires", Stamp(previous), Stamp(renewed), DebugWriteScope.Live, reason);
        }
        else
        {
            DebugWrite.Record(context, "gm stream " + channel, "subscribed", "off", "on", DebugWriteScope.Live, reason);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Following [{channel}] ({ScopeText(scope)}); it turns off after {stream.Options.AutoOffAfter.TotalMinutes:0.##} min or when you log out."));
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
            IReadOnlyList<(string Channel, DateTimeOffset ExpiresAt)> followed = stream.Subscriptions(gm);
            int stopped = stream.UnsubscribeAll(gm);
            foreach ((string followedChannel, _) in followed)
            {
                DebugWrite.Record(context, "gm stream " + followedChannel, "subscribed", "on", "off", DebugWriteScope.Live);
            }

            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Stopped {stopped} channel(s)."));
        }
        else
        {
            bool was = stream.Unsubscribe(gm, channel);
            if (was)
            {
                DebugWrite.Record(context, "gm stream " + channel, "subscribed", "on", "off", DebugWriteScope.Live);
            }

            context.Reply(was ? $"Stopped following [{channel}]." : $"You do not follow [{channel}].");
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

        IReadOnlyList<GmStreamSubscription> subscriptions = stream.Details(context.Player.Guid);
        if (subscriptions.Count == 0)
        {
            context.Reply("You follow no stream channels.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (GmStreamSubscription subscription in subscriptions)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"[{subscription.Channel}] turns off in {Math.Max(0, (int)(subscription.ExpiresAt - now).TotalSeconds)} s, scope {ScopeText(subscription.Scope)}"));
        }

        context.Reply("Available channels (.arcane stream on <channel> [scope]):");
        foreach (GmStreamChannelInfo info in GmStreamChannels.All)
        {
            context.Reply(info.AllowsAll ? $"{info.Name} [target|near|all]: {info.Carries}" : $"{info.Name} [target|near]: {info.Carries}");
        }

        return true;
    }
}

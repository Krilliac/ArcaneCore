using System.Globalization;
using System.Text;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// <c>.event</c> (vmangos Chat.cpp:373-382, ServerCommands.cpp:639-883): <c>.event list [all]</c>, <c>.event &lt;id&gt;</c> (info),
/// <c>.event start|stop &lt;id&gt;</c> and <c>.event enable|disable &lt;id&gt;</c>. The id may be a number or the shift-click link
/// <c>|cffffffff|Hgameevent:&lt;id&gt;|h[name]|h|r</c>. Account levels are the vmangos ones (list and info 3, start and stop 4, enable
/// and disable 5), mapped onto ArcaneCore's four tiers: GameMaster, Administrator, Administrator. Start and stop pass
/// <c>overwrite = true</c> like the originals. Texts: mangos_string 583-588 and 1130-1131 are in classic-db and used verbatim; the
/// vmangos-only ids 1600-1603 (event disabled / enabled / already enabled / already disabled) are not in any dump here, so the
/// literal English below is ArcaneCore's wording.
/// </summary>
public sealed class GameEventCommands : ICommandGroup
{
    // classic-db mangos_string
    public const string EntryListText = "{0} - |cffffffff|Hgameevent:{0}|h[{1}]|h|r{2}";   // 583
    public const string NoEventFoundText = "No event found!";                                // 584
    public const string EventNotExistText = "Event not exist!";                              // 585
    public const string EventInfoText = "Event {0}: {1}{2}Start: {3} End: {4} Occurence: {5} Length: {6}Next state change: {7}"; // 586
    public const string AlreadyActiveText = "Event {0} already active!";                     // 587
    public const string NotActiveText = "Event {0} not active!";                             // 588
    public const string StartedText = "event started {0} \"{1}\"";                           // 1130
    public const string StoppedText = "event stopped {0} \"{1}\"";                           // 1131
    public const string ActiveStateText = " [active]";                                       // 35
    public const string InactiveStateText = " [inactive]";                                   // 317

    // vmangos-only (1600-1603): ArcaneCore wording
    public const string IsDisabledText = "Event {0} is disabled!";
    public const string EnabledText = "event enabled {0} \"{1}\"";
    public const string DisabledText = "event disabled {0} \"{1}\"";
    public const string AlreadyEnabledText = "Event {0} already enabled!";
    public const string AlreadyDisabledText = "Event {0} already disabled!";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "event", AccountSecurity.GameMaster,
            "Syntax: .event #event_id\nShow the information of an event.",
            Info,
            [
                new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .event list [all]\nShow the running events, or all of them with 'all'.", List, RetailLevel: 3),
                new ChatCommand("start", AccountSecurity.Administrator, "Syntax: .event start #event_id\nStart an event now (it is rescheduled to match).", Start, RetailLevel: 4),
                new ChatCommand("stop", AccountSecurity.Administrator, "Syntax: .event stop #event_id\nStop a running event now.", Stop, RetailLevel: 4),
                new ChatCommand("enable", AccountSecurity.Administrator, "Syntax: .event enable #event_id\nEnable an event again (stored in game_event.disabled).", Enable, RetailLevel: 5),
                new ChatCommand("disable", AccountSecurity.Administrator, "Syntax: .event disable #event_id\nDisable an event: a running one stops and the schedule never starts it (stored in game_event.disabled).", Disable, RetailLevel: 5),
            ],
            RetailLevel: 3),
    ];

    private static GameEventService? Service(CommandContext context) => context.Session.Services.GetService<GameEventFeature>()?.Service;

    private static bool List(CommandContext context, string args)
    {
        bool all = args.Trim().Equals("all", StringComparison.Ordinal);
        int counter = 0;
        if (Service(context) is { } service)
        {
            foreach (GameEventDefinition definition in service.Events.Where(e => service.IsValidEvent(e.Id)))
            {
                bool active = service.IsActiveEvent(definition.Id);
                if (!active && !all)
                {
                    continue;
                }

                context.Reply(string.Format(CultureInfo.InvariantCulture, EntryListText, definition.Id, definition.Description, active ? ActiveStateText : InactiveStateText));
                counter++;
            }
        }

        if (counter == 0)
        {
            context.Reply(NoEventFoundText);
        }

        return true;
    }

    private static bool Info(CommandContext context, string args)
    {
        if (!TryGetEvent(context, args, out GameEventService? service, out GameEventDefinition? definition, out bool handled))
        {
            return handled;
        }

        WorldStateHooks hooks = WorldStateHooks.For(context.World);
        TimeZoneInfo zone = hooks.LocalZone;
        DateTimeOffset now = hooks.Time.UtcNow;
        uint delay = GameEventSchedule.NextCheckSeconds(definition!, now, hooks.GameEventSettings.LeapDayMode);
        DateTimeOffset next = now.AddSeconds(delay);
        string nextText = next >= definition!.Start && next < definition.End ? Stamp(next, zone) : "-";
        context.Reply(string.Format(
            CultureInfo.InvariantCulture, EventInfoText,
            definition.Id, definition.Description, service!.IsActiveEvent(definition.Id) ? ActiveStateText : string.Empty,
            Stamp(definition.Start, zone), Stamp(definition.End, zone),
            SecsToTimeString(definition.OccurenceMinutes * 60L), SecsToTimeString(definition.LengthMinutes * 60L), nextText));
        return true;
    }

    private static bool Start(CommandContext context, string args)
    {
        if (!TryGetEvent(context, args, out GameEventService? service, out GameEventDefinition? definition, out bool handled))
        {
            return handled;
        }

        if (service!.IsActiveEvent(definition!.Id))
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, AlreadyActiveText, definition.Id));
            return true;
        }

        if (definition.Disabled)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, IsDisabledText, definition.Id));
            return true;
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, StartedText, definition.Id, definition.Description));
        service.StartEvent(definition.Id, overwrite: true);
        return true;
    }

    private static bool Stop(CommandContext context, string args)
    {
        if (!TryGetEvent(context, args, out GameEventService? service, out GameEventDefinition? definition, out bool handled))
        {
            return handled;
        }

        if (!service!.IsActiveEvent(definition!.Id))
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, NotActiveText, definition.Id));
            return true;
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, StoppedText, definition.Id, definition.Description));
        service.StopEvent(definition.Id, overwrite: true);
        return true;
    }

    private static bool Enable(CommandContext context, string args)
    {
        if (!TryGetEvent(context, args, out GameEventService? service, out GameEventDefinition? definition, out bool handled))
        {
            return handled;
        }

        if (!definition!.Disabled)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, AlreadyEnabledText, definition.Id));
            return true;
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, EnabledText, definition.Id, definition.Description));
        service!.EnableEvent(definition.Id, enable: true);
        return true;
    }

    private static bool Disable(CommandContext context, string args)
    {
        if (!TryGetEvent(context, args, out GameEventService? service, out GameEventDefinition? definition, out bool handled))
        {
            return handled;
        }

        if (definition!.Disabled)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, AlreadyDisabledText, definition.Id));
            return true;
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, DisabledText, definition.Id, definition.Description));
        service!.EnableEvent(definition.Id, enable: false);
        return true;
    }

    /// <summary>
    /// The shared head of the handlers: the id (a number or a <c>Hgameevent</c> link), the event it names and that it is valid.
    /// <paramref name="handled"/> is what the handler returns when this fails: false (show the syntax) for a bad argument, true after
    /// the "Event not exist!" reply.
    /// </summary>
    private static bool TryGetEvent(CommandContext context, string args, out GameEventService? service, out GameEventDefinition? definition, out bool handled)
    {
        service = Service(context);
        definition = null;
        if (!TryParseEventId(args, out uint id))
        {
            handled = false;
            return false;
        }

        handled = true;
        if (service is null || id > ushort.MaxValue || !service.IsValidEvent((ushort)id))
        {
            context.Reply(EventNotExistText);
            return false;
        }

        definition = service.Find((ushort)id);
        return definition is not null;
    }

    /// <summary>vmangos <c>ExtractUint32KeyFromLink(&amp;args, "Hgameevent", id)</c>: a number, or the id inside a shift-click event link.</summary>
    internal static bool TryParseEventId(string args, out uint id)
    {
        id = 0;
        string text = args.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.StartsWith('|'))
        {
            const string Key = "Hgameevent:";
            int at = text.IndexOf(Key, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            int start = at + Key.Length;
            int end = start;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            return uint.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out id);
        }

        int length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length]))
        {
            length++;
        }

        return length > 0 && uint.TryParse(text.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>vmangos <c>TimeToTimestampStr</c> (Util.cpp:284-296): <c>YYYY-MM-DD_HH-MM-SS</c> in the local zone.</summary>
    internal static string Stamp(DateTimeOffset instant, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{local.Year:D4}-{local.Month:D2}-{local.Day:D2}_{local.Hour:D2}-{local.Minute:D2}-{local.Second:D2}");
    }

    /// <summary>vmangos <c>secsToTimeString(secs)</c> in its long form (shared/Util.cpp:197-250): "1 Day 2 Hours 5 Minutes " and so on.</summary>
    internal static string SecsToTimeString(long timeInSecs)
    {
        long secs = timeInSecs % 60;
        long minutes = timeInSecs % 3600 / 60;
        long hours = timeInSecs % 86400 / 3600;
        long days = timeInSecs / 86400;
        var text = new StringBuilder();
        if (days != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{days}").Append(days == 1 ? " Day " : " Days ");
        }

        if (hours != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{hours}").Append(hours <= 1 ? " Hour " : " Hours ");
        }

        if (minutes != 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"{minutes}").Append(minutes == 1 ? " Minute " : " Minutes ");
        }

        if (secs != 0 || (days == 0 && hours == 0 && minutes == 0))
        {
            text.Append(CultureInfo.InvariantCulture, $"{secs}").Append(secs <= 1 ? " Second." : " Seconds.");
        }

        return text.ToString();
    }
}

/// <summary><c>.lookup event &lt;name&gt;</c> (vmangos LookupCommands.cpp:1480-1526; Chat.cpp:563 SEC_TICKETMASTER): events whose description contains the text, case-insensitively.</summary>
public sealed class LookupEventCommand : ICommandExtension
{
    public string Path => "lookup";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("event", AccountSecurity.Moderator, "Syntax: .lookup event $namepart\nList the events whose description contains the text.", Lookup, RetailLevel: 2),
    ];

    private static bool Lookup(CommandContext context, string args)
    {
        string name = args.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        int counter = 0;
        if (context.Session.Services.GetService<GameEventFeature>()?.Service is { } service)
        {
            foreach (GameEventDefinition definition in service.Events)
            {
                if (!service.IsValidEvent(definition.Id) || definition.Description.Length == 0
                    || !definition.Description.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string state = service.IsActiveEvent(definition.Id) ? GameEventCommands.ActiveStateText : string.Empty;
                context.Reply(string.Format(CultureInfo.InvariantCulture, GameEventCommands.EntryListText, definition.Id, definition.Description, state));
                counter++;
            }
        }

        if (counter == 0)
        {
            context.Reply(GameEventCommands.NoEventFoundText);
        }

        return true;
    }
}

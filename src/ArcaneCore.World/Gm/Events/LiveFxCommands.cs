using System.Globalization;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Events;

/// <summary>
/// <c>.fx</c>: non-retail GM tooling that pushes live, client-visible effects with packets the stock 1.12.1 client
/// already understands (Krilliac server-Zero branch <c>claude/live-override-commands-plan</c>, trimmed to build 5875:
/// no SMSG_OVERRIDE_LIGHT, which is not a 5875 opcode, and no movie). Each effect goes to a scope
/// (<see cref="FxScopes"/>: self, target, zone, map, server; default self). Nothing here changes server state except
/// <c>.fx weather</c>, which drives the same zone weather as <c>.wchange</c>. The vmangos counterparts are the
/// invoker-only <c>.debug play music|sound|cinematic</c> and <c>.debug worldstate</c> (Chat.cpp:288-323); the root
/// exists only while <see cref="GmOptions.LiveFx"/> is on. Every use is written to the GM audit log like any other
/// command above level 0 (<see cref="GmCommandLog"/>).
/// </summary>
public sealed class LiveFxCommands : ICommandGroup
{
    /// <summary>The reply after an effect was sent: effect, recipient count, scope word.</summary>
    public const string SentText = "{0} sent to {1} player(s) ({2}).";

    /// <summary>The reply when an area id is not in AreaTable.</summary>
    public const string AreaNotFoundText = "Area {0} not found.";

    /// <summary>The reply when <c>.fx weather</c> is given a per-player scope.</summary>
    public const string WeatherScopeText = "Weather belongs to a zone: use zone, map or server.";

    /// <summary>The reply when <c>.fx event</c> names no known preset.</summary>
    public const string UnknownPresetText = "Unknown preset '{0}'. Presets: {1}";

    /// <summary>The header of the preset list.</summary>
    public const string PresetListText = "Presets: {0}";

    /// <summary>The longest screen message in UTF-8 bytes (vmangos formats it into a 1024-byte buffer, WorldSession.cpp:885-889).</summary>
    public const int MaxMessageBytes = 1023;

    /// <summary>The fastest client clock <c>.fx timespeed</c> accepts, in game minutes per real second (a game day in 24 s).</summary>
    public const float MaxMinutesPerSecond = 60f;

    private const string ScopeHelp = "[self|target|zone|map|server] (default self; map and server need an Administrator)";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand(
            "fx", AccountSecurity.GameMaster,
            "Syntax: .fx $subcommand\nNon-retail GM tooling: push music, sounds, spell visuals, cinematics, world states and screen text to a scope.",
            null,
            [
                new ChatCommand("music", AccountSecurity.GameMaster, "Syntax: .fx music #soundid " + ScopeHelp + "\nPlay a SoundEntries id as music (SMSG_PLAY_MUSIC).", Music),
                new ChatCommand("sound", AccountSecurity.GameMaster, "Syntax: .fx sound #soundid " + ScopeHelp + "\nPlay a SoundEntries id (SMSG_PLAY_SOUND).", Sound),
                new ChatCommand("visual", AccountSecurity.GameMaster, "Syntax: .fx visual #kitid " + ScopeHelp + "\nPlay a SpellVisualKit on each scoped character, seen by those around it (SMSG_PLAY_SPELL_VISUAL).", Visual),
                new ChatCommand("cinematic", AccountSecurity.GameMaster, "Syntax: .fx cinematic #cinematicid " + ScopeHelp + "\nStart a CinematicSequences id (SMSG_TRIGGER_CINEMATIC).", Cinematic),
                new ChatCommand("zoneattack", AccountSecurity.GameMaster, "Syntax: .fx zoneattack [#areaid] " + ScopeHelp + "\nShow \"<zone> is under attack!\" for an area (default your zone) (SMSG_ZONE_UNDER_ATTACK).", ZoneAttack),
                new ChatCommand("worldstate", AccountSecurity.GameMaster, "Syntax: .fx worldstate #field #value " + ScopeHelp + "\nSet a world-state HUD value on the clients (SMSG_UPDATE_WORLD_STATE).", WorldState),
                new ChatCommand("timespeed", AccountSecurity.GameMaster, "Syntax: .fx timespeed #minutespersecond|reset " + ScopeHelp + "\nRun the client day/night clock at a speed (0 freezes it, reset is the retail 1/60, at most 60); client-side only, a relog restores it (SMSG_LOGIN_SETTIMESPEED).", TimeSpeed),
                new ChatCommand("message", AccountSecurity.GameMaster, "Syntax: .fx message [self|target|zone|map|server] $text\nShow large text in the middle of the screen (SMSG_AREA_TRIGGER_MESSAGE).", Message),
                new ChatCommand("weather", AccountSecurity.Administrator, "Syntax: .fx weather #weathertype #status [zone|map|server]\n.wchange for your zone (default), every occupied zone of your map, or of every map.", Weather),
                new ChatCommand("event", AccountSecurity.GameMaster, "Syntax: .fx event [$preset] " + ScopeHelp + "\nFire a named set of effects at once; no preset lists them.", Event),
            ]),
    ];

    /// <summary>Registered only while <c>World:GmCommands:LiveFx</c> is on (default on; a host without configuration keeps it).</summary>
    public bool IsEnabled(IServiceProvider? services)
        => services?.GetService<IConfiguration>() is not { } configuration || GmOptions.Bind(configuration).LiveFx;

    /// <summary>One named composition of effects; every step is sent to every recipient.</summary>
    public sealed record Preset(string Name, string Description, IReadOnlyList<Func<Player, (WorldOpcode Opcode, byte[] Payload)>> Steps);

    /// <summary>MUSIC_DARKMOON_FAIRE_MUSIC (vmangos scripts/world/go_scripts.cpp:275).</summary>
    public const uint DarkmoonFaireMusic = 8440;

    /// <summary>SOUND_CHEER_1 (vmangos scripts/world/fireworks_show.cpp:51).</summary>
    public const uint CheerSound = 8574;

    /// <summary>SOUND_BG_START, the battleground start horn (vmangos Battlegrounds/BattleGroundDefines.h:45).</summary>
    public const uint BattleStartSound = 3439;

    /// <summary>The <c>.fx event</c> presets. Every id is one vmangos itself sends; the texts are ArcaneCore's.</summary>
    public static IReadOnlyList<Preset> Presets { get; } =
    [
        new("faire", "Darkmoon Faire music and a welcome",
        [
            _ => (WorldOpcode.SmsgPlayMusic, LiveFxPackets.PlayMusic(DarkmoonFaireMusic)),
            _ => (WorldOpcode.SmsgAreaTriggerMessage, LiveFxPackets.ScreenMessage("The Darkmoon Faire has come to town!")),
        ]),
        new("celebrate", "a crowd cheer",
        [
            _ => (WorldOpcode.SmsgPlaySound, LiveFxPackets.PlaySound(CheerSound)),
        ]),
        new("invasion", "battle horn, zone-under-attack alert for each player's own zone, and a warning",
        [
            _ => (WorldOpcode.SmsgPlaySound, LiveFxPackets.PlaySound(BattleStartSound)),
            p => (WorldOpcode.SmsgZoneUnderAttack, LiveFxPackets.ZoneUnderAttack(p.ZoneId)),
            _ => (WorldOpcode.SmsgAreaTriggerMessage, LiveFxPackets.ScreenMessage("Your zone is under attack!")),
        ]),
    ];

    private static bool Music(CommandContext context, string args)
        => SimpleU32(context, args, "Hsound", "Music", id => (WorldOpcode.SmsgPlayMusic, LiveFxPackets.PlayMusic(id)));

    private static bool Sound(CommandContext context, string args)
        => SimpleU32(context, args, "Hsound", "Sound", id => (WorldOpcode.SmsgPlaySound, LiveFxPackets.PlaySound(id)));

    private static bool Cinematic(CommandContext context, string args)
        => SimpleU32(context, args, null, "Cinematic", id => (WorldOpcode.SmsgTriggerCinematic, LiveFxPackets.TriggerCinematic(id)));

    private static bool Visual(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32(out uint kit) || !TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        if (FxScopes.Resolve(context, scope) is not { } recipients)
        {
            return true;
        }

        foreach (Player player in recipients)
        {
            // vmangos Unit::SendPlaySpellVisualKit: SendMessageToSet(packet, self = true).
            byte[] packet = LiveFxPackets.PlaySpellVisual(player.Guid, kit);
            player.Session.Send(WorldOpcode.SmsgPlaySpellVisual, packet);
            player.Map?.BroadcastToObservers(player, WorldOpcode.SmsgPlaySpellVisual, packet);
        }

        Sent(context, "Spell visual", recipients.Count, scope);
        return true;
    }

    private static bool ZoneAttack(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptUInt32(out uint area, 0))
        {
            // Not a number: the only argument may be the scope.
            area = 0;
        }

        if (!TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        area = area == 0 ? context.Player.ZoneId : area;
        AreaTable areas = WorldMaps.Of(context.World).Areas;
        if (areas.Count > 0 && areas.GetById(area) is null)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, AreaNotFoundText, area));
            return true;
        }

        return Send(context, scope, "Zone attack", _ => (WorldOpcode.SmsgZoneUnderAttack, LiveFxPackets.ZoneUnderAttack(area)));
    }

    private static bool WorldState(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32(out uint field) || !parsed.ExtractUInt32(out uint value) || !TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        return Send(context, scope, "World state", _ => (WorldOpcode.SmsgUpdateWorldState, LiveFxPackets.UpdateWorldState(field, value)));
    }

    private static bool TimeSpeed(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        float speed;
        if (parsed.ExtractLiteral("reset") is not null)
        {
            speed = GameTimePacker.GameSpeedMinutesPerSecond;
        }
        else if (!parsed.ExtractFloat(out speed) || !float.IsFinite(speed) || speed < 0 || speed > MaxMinutesPerSecond)
        {
            return false;
        }

        if (!TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        DateTimeOffset now = WorldStateHooks.For(context.World).LocalNow();
        return Send(context, scope, "Time speed", _ => (WorldOpcode.SmsgLoginSettimespeed, LiveFxPackets.TimeSpeed(now, speed)));
    }

    private static bool Message(CommandContext context, string args)
    {
        string text = args.Trim();
        FxScope scope = FxScope.Self;
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string first = space < 0 ? text : text[..space];
        if (FxScopes.TryParse(first, out FxScope parsed))
        {
            scope = parsed;
            text = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        }

        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaxMessageBytes)
        {
            return false;
        }

        return Send(context, scope, "Message", _ => (WorldOpcode.SmsgAreaTriggerMessage, LiveFxPackets.ScreenMessage(text)));
    }

    private static bool Weather(CommandContext context, string args)
    {
        if (!WorldStateHooks.For(context.World).WeatherSettings.Enabled)
        {
            context.Reply(WeatherCommands.WeatherDisabledText);
            return true;
        }

        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        FxScope scope = FxScope.Zone;
        if (parts.Length == 3)
        {
            if (!FxScopes.TryParse(parts[2], out scope))
            {
                return false;
            }

            if (scope is FxScope.Self or FxScope.Target)
            {
                context.Reply(WeatherScopeText);
                return true;
            }

            parts = parts[..2];
        }

        if (!WeatherCommands.TryParse(parts, out WeatherType type, out float grade))
        {
            return false;
        }

        if (FxScopes.Resolve(context, scope) is not { } recipients)
        {
            return true;
        }

        // The weather of each (map, zone) the scope's players stand in: the zone state changes and MapWeather tells that
        // zone's players, exactly as .wchange does for one zone.
        int zones = 0;
        foreach (IGrouping<(Map Map, uint Zone), Player> group in recipients
            .Where(p => p.Map is not null)
            .GroupBy(p => (p.Map!, p.ZoneId)))
        {
            if (group.Key.Map.FindUpdater<MapWeather>() is { } weather)
            {
                weather.SetWeather(group.Key.Zone, type, grade, permanent: false);
                zones++;
            }
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, "Weather set in {0} zone(s) ({1}).", zones, FxScopes.Word(scope)));
        return true;
    }

    private static bool Event(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        string names = string.Join(", ", Presets.Select(p => p.Name + " (" + p.Description + ")"));
        string? name = parsed.ExtractLiteral();
        if (name is null)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, PresetListText, names));
            return true;
        }

        Preset? preset = Presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            context.Reply(string.Format(CultureInfo.InvariantCulture, UnknownPresetText, name, names));
            return true;
        }

        if (!TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        if (FxScopes.Resolve(context, scope) is not { } recipients)
        {
            return true;
        }

        foreach (Player player in recipients)
        {
            foreach (Func<Player, (WorldOpcode Opcode, byte[] Payload)> step in preset.Steps)
            {
                (WorldOpcode opcode, byte[] payload) = step(player);
                player.Session.Send(opcode, payload);
            }
        }

        Sent(context, "Event " + preset.Name, recipients.Count, scope);
        return true;
    }

    /// <summary>"#id [scope]" where the id may also be a shift-link of <paramref name="linkType"/>.</summary>
    private static bool SimpleU32(CommandContext context, string args, string? linkType, string effect, Func<uint, (WorldOpcode, byte[])> build)
    {
        var parsed = new CommandArgs(args);
        uint id;
        if (!(linkType is null ? parsed.ExtractUInt32(out id) : parsed.ExtractUInt32KeyFromLink(linkType, out id)) || !TryScope(parsed, out FxScope scope))
        {
            return false;
        }

        return Send(context, scope, effect, _ => build(id));
    }

    /// <summary>The optional trailing scope word; false when something else (or more) is left.</summary>
    private static bool TryScope(CommandArgs parsed, out FxScope scope)
    {
        scope = FxScope.Self;
        if (parsed.IsEmpty)
        {
            return true;
        }

        return FxScopes.TryParse(parsed.ExtractLiteral(), out scope) && parsed.IsEmpty;
    }

    private static bool Send(CommandContext context, FxScope scope, string effect, Func<Player, (WorldOpcode Opcode, byte[] Payload)> build)
    {
        if (FxScopes.Resolve(context, scope) is not { } recipients)
        {
            return true;
        }

        foreach (Player player in recipients)
        {
            (WorldOpcode opcode, byte[] payload) = build(player);
            player.Session.Send(opcode, payload);
        }

        Sent(context, effect, recipients.Count, scope);
        return true;
    }

    private static void Sent(CommandContext context, string effect, int count, FxScope scope)
        => context.Reply(string.Format(CultureInfo.InvariantCulture, SentText, effect, count, FxScopes.Word(scope)));
}

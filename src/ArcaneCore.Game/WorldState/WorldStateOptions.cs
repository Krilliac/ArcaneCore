namespace ArcaneCore.Game.WorldState;

/// <summary>
/// Where CMSG_ZONEUPDATE and the periodic check get a player's zone from. vmangos always derives
/// it from terrain (MiscHandler.cpp:381-386 HandleZoneUpdateOpcode ignores the client value), so
/// <see cref="Never"/> is the retail behaviour. <see cref="Auto"/> is a development-world
/// allowance: the client value (and the stored zone) is used only while zones cannot be derived (no area data or no terrain files).
/// </summary>
public enum ClientZoneTrust
{
    /// <summary>Trust the client zone only while zones cannot be derived (no area_template imported or no terrain files).</summary>
    Auto,

    /// <summary>Retail: always derive the zone from terrain and the area table.</summary>
    Never,

    /// <summary>Always use the client / stored zone and never derive it (development).</summary>
    Always,
}

/// <summary>Zone and area tracking options (configuration section <c>World:Zones</c>).</summary>
public sealed class ZoneOptions
{
    public const string SectionName = "World:Zones";

    /// <summary>
    /// See <see cref="ClientZoneTrust"/>. Default <see cref="ClientZoneTrust.Auto"/>: a world with
    /// imported area data behaves like retail; one without keeps accepting the client zone.
    /// This is a documented development allowance, not retail behaviour.
    /// </summary>
    public ClientZoneTrust ClientZoneTrust { get; set; } = ClientZoneTrust.Auto;

    /// <summary>
    /// vmangos <c>Movement.RelocationVmapsCheckDelay</c> (default 0, at most 2000): when above 0 the
    /// explore check runs this many ms after a position change instead of at once (Player.cpp:5975-5985).
    /// </summary>
    public uint RelocationCheckDelayMs { get; set; }

    /// <summary>
    /// The realm kind for PvP rules (vmangos <c>IsPvPRealm</c> / <c>IsFFAPvPRealm</c>): Normal (default), Pvp or FfaPvp.
    /// </summary>
    public Zones.PvpRealmMode PvpRealmMode { get; set; }
}

/// <summary>Game-time options (configuration section <c>World:Time</c>).</summary>
public sealed class TimeOptions
{
    public const string SectionName = "World:Time";

    /// <summary>
    /// Pack the SERVER's local time into SMSG_LOGIN_SETTIMESPEED and compute weather seasons and
    /// event dates in it, as vmangos does with <c>localtime</c> (Server/Packets/Misc.cpp:924-933,
    /// Weather.cpp:100-103). Default true (retail); false uses UTC.
    /// </summary>
    public bool UseServerLocalTime { get; set; } = true;

    /// <summary>
    /// An explicit zone id (IANA or Windows) to treat as "server local time"; empty = the machine's
    /// zone. Only read while <see cref="UseServerLocalTime"/> is true.
    /// </summary>
    public string TimeZoneId { get; set; } = "";
}

/// <summary>Weather options (configuration section <c>World:Weather</c>).</summary>
public sealed class WeatherOptions
{
    public const string SectionName = "World:Weather";

    /// <summary>vmangos <c>ActivateWeather</c> (default 1, World.cpp:730).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>vmangos <c>ChangeWeatherInterval</c>: ms between weather regenerations of a zone (default 10 minutes, World.cpp:596).</summary>
    public uint ChangeIntervalMs { get; set; } = 10 * 60 * 1000;
}

/// <summary>Exploration options (configuration section <c>World:Exploration</c>).</summary>
public sealed class ExplorationOptions
{
    public const string SectionName = "World:Exploration";

    /// <summary>vmangos <c>Rate.XP.Explore</c> (default 1).</summary>
    public float RateXp { get; set; } = 1.0f;

    /// <summary>
    /// <c>.explorecheat</c> as vmangos writes it (CharacterCommands.cpp:640-679) is buggy: it announces the
    /// SELECTED player but changes the ISSUER's fields, and "0" ORs in 0 (does nothing). Default false
    /// (retail/vmangos). True applies the effect to the selected player and really clears on 0.
    /// </summary>
    public bool CorrectExploreCheat { get; set; }
}

/// <summary>World-state options (configuration section <c>World:WorldStates</c>).</summary>
public sealed class WorldStatesOptions
{
    public const string SectionName = "World:WorldStates";

    /// <summary>
    /// A JSON file holding the default world states sent with every zone entry, as <c>[[state, value], ...]</c>
    /// (vmangos hard-codes 108 sniffed pairs, Player.cpp:8041-8213; that table is GPL data and is not
    /// shipped here). Empty (default) = no default pairs, like mangos-classic.
    /// </summary>
    public string DefaultsPath { get; set; } = "";
}

/// <summary>Game-event options (configuration section <c>World:GameEvents</c>).</summary>
public sealed class GameEventOptions
{
    public const string SectionName = "World:GameEvents";

    /// <summary>
    /// How yearly events count February 29th (<see cref="Events.LeapDayMode"/>). Default
    /// <c>DateStable</c> (holidays keep their calendar date, as in retail); <c>VmangosLiteral</c> reproduces the
    /// vmangos loop, whose yearly events start a day late in many years.
    /// </summary>
    public Events.LeapDayMode LeapDayMode { get; set; } = Events.LeapDayMode.DateStable;

    /// <summary>
    /// Whether the game-event service runs at all. Default true. With false no event ever starts: holiday
    /// content, event quests and event spawns stay off (the pre-wave-4 behaviour).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Which <c>game_event</c> table layout the data is in (<see cref="Events.GameEventDialect"/>). Default
    /// <c>Auto</c>: decided from the table's own columns when it is loaded; set it only to override a wrong guess.
    /// </summary>
    public Events.GameEventDialect Dialect { get; set; } = Events.GameEventDialect.Auto;

    /// <summary>
    /// Whether an event is active AT its start instant (vmangos <c>start &lt;= current</c>, GameEventMgr.cpp:41) or only
    /// after it (mangos-classic <c>start &lt; current</c>, GameEventMgr.cpp:36-40). Default <c>Auto</c>: the rule of the
    /// table's dialect (the vmangos rule for vmangos tables, the mangos-classic rule for classic-db tables).
    /// </summary>
    public Events.GameEventStartBoundary StartBoundary { get; set; } = Events.GameEventStartBoundary.Auto;

    /// <summary>
    /// How a <c>start_time</c> / <c>end_time</c> that has no zone is read. Default <c>Wall</c> (vmangos: MySQL
    /// <c>UNIX_TIMESTAMP</c> reads the session zone, GameEventMgr.cpp:183); <c>StandardTime</c> reproduces
    /// mangos-classic, whose <c>std::mktime</c> on a zeroed <c>tm</c> never applies daylight saving
    /// (Field.cpp:24-31).
    /// </summary>
    public Events.GameEventDateTimeInterpretation DateTimeInterpretation { get; set; } = Events.GameEventDateTimeInterpretation.Wall;

    /// <summary>
    /// How yearly (<c>schedule_type</c> 11) events are moved to the current year (<see cref="Events.YearlyRebaseMode"/>).
    /// Default <c>SpanNewYear</c> (a holiday that crosses New Year keeps running, as in retail); <c>MangosLiteral</c> is
    /// the mangos-classic code, which cuts such a holiday at December 31st.
    /// </summary>
    public Events.YearlyRebaseMode YearlyRebase { get; set; } = Events.YearlyRebaseMode.SpanNewYear;

    /// <summary>
    /// mangos-classic never restores a serverside (<c>schedule_type</c> 0) event that was active at shutdown (its
    /// Update skips them and CheckOneGameEvent is false for their far-future start, GameEventMgr.cpp:634-690), vmangos
    /// leaves that state to its hardcoded handlers. Default false (retail); true re-applies the serverside events
    /// recorded in <c>game_event_status</c> with the resume flag, so their progress is not lost.
    /// </summary>
    public bool RestoreServersideEvents { get; set; }

    /// <summary>
    /// vmangos <c>Event.Announce</c> (GameEventMgr.cpp:788-789): tell every player in the world when an event starts.
    /// Default false (retail default).
    /// </summary>
    public bool Announce { get; set; }

    /// <summary>
    /// Registers a <c>.reload game_event</c> sub-command. Retail has none, so the default is false; it still needs
    /// <c>HotReload:Commands</c> like every reload.
    /// </summary>
    public bool AllowReload { get; set; }
}

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

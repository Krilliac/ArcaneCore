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
}

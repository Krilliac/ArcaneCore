namespace ArcaneCore.Game.Instances;

/// <summary>
/// Instance settings, bound from the <c>World:Instances</c> configuration section. Defaults are
/// vmangos' (<c>mangosd.conf.dist</c> and MapPersistentStateMgr.cpp).
/// </summary>
public sealed class InstanceOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "World:Instances";

    /// <summary>vmangos <c>MIN_UNLOAD_DELAY</c> (Map.h): 1 ms, i.e. "at the next update".</summary>
    public const int MinUnloadDelayMs = 1;

    /// <summary>
    /// How long an empty instance map stays loaded before it is unloaded (vmangos
    /// <c>Instance.UnloadDelay</c>, default 30 minutes). Its save and binds survive the unload.
    /// </summary>
    public int UnloadDelayMs { get; set; } = 30 * 60 * 1000;

    /// <summary>
    /// A normal (non-raid) dungeon resets this long after it was created, but only while nobody is
    /// inside (vmangos <c>MapPersistentStateManager::AddPersistentState</c>: "if no creatures are
    /// killed the instance will reset in two hours"; the schedule is cancelled while players are in).
    /// </summary>
    public int NormalDungeonResetSeconds { get; set; } = 2 * 60 * 60;

    /// <summary>Hour of the day (UTC, 0–23) of global raid resets (vmangos <c>Instance.ResetTimeHour</c>, default 4).</summary>
    public int ResetTimeHour { get; set; } = 4;

    /// <summary>
    /// Time a player who lost the right to be in an instance (left the group, global reset) has
    /// before being sent to its bind point (vmangos <c>Player::UpdateHomebindTime</c>: 60 s).
    /// </summary>
    public int HomebindTimerMs { get; set; } = 60_000;

    /// <summary>A creature kill in a normal dungeon moves the reset time to respawn + 2 h when later (vmangos Map::BindToInstanceOrRaid, Map.cpp:3536-3544). Default on (retail).</summary>
    public bool ResetExtendsOnKills { get; set; } = true;

    /// <summary>
    /// New instances one account may enter per hour (vmangos <c>Instance.PerHourLimit</c>, default 5;
    /// <c>MAX_INSTANCE_PER_ACCOUNT_PER_HOUR</c> Player.h:669). 0 turns the limit off (a ArcaneCore
    /// convention: vmangos would refuse every new instance at 0). Game masters are exempt.
    /// </summary>
    public int PerHourLimit { get; set; } = 5;

    /// <summary>
    /// Seconds between two "Please leave the instance so it can be reset." notices to the players inside an instance that a refused
    /// personal reset asks to leave (an ArcaneCore limit: the refusal itself is an ArcaneCore choice, see docs/integration/instances.md,
    /// and the requester can repeat CMSG_RESET_INSTANCES at will). The requester still gets SMSG_INSTANCE_RESET_FAILED every time.
    /// 0 sends the notice on every refusal.
    /// </summary>
    public int ResetRefusedNoticeSeconds { get; set; } = 10;

    /// <summary>Let players enter raids without a raid group (vmangos <c>Instance.IgnoreRaid</c>, default off).</summary>
    public bool IgnoreRaidGroup { get; set; }

    /// <summary>Unload delay actually used (at least <see cref="MinUnloadDelayMs"/>).</summary>
    public uint EffectiveUnloadDelayMs => (uint)Math.Max(UnloadDelayMs, MinUnloadDelayMs);
}

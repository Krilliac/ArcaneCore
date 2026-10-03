namespace ArcaneCore.Game.Combat;

/// <summary>
/// Duel settings, bound from the <c>World:Duel</c> configuration section (restart-only). Every default is the vmangos
/// (primary reference) value; a deviation is a deliberate switch.
/// </summary>
public sealed class DuelOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "World:Duel";

    /// <summary>Master switch. Off, the duel spell refuses with SPELL_FAILED_NO_DUELING. Default on (every character knows spell 7266, playercreateinfo_spell).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between the accept and the start (vmangos Player::UpdateDuelFlag, Player.cpp:17248-17263: +3). The countdown packet always says 3000 ms (DuelHandler.cpp:45).</summary>
    public int StartDelaySeconds { get; set; } = 3;

    /// <summary>
    /// Yards from the flag at which the out-of-bounds warning starts: 75 in vmangos ("Nostalrius: modified duel distance (50 -> 75m)",
    /// Player.cpp:6688-6716), 50 in mangos-classic (Player.cpp:6917-6939). Which one is true retail cannot be settled from the references.
    /// </summary>
    public float OutOfBoundsYards { get; set; } = 75f;

    /// <summary>Yards within which a player who left the area counts as back: 70 in vmangos, 40 in mangos-classic.</summary>
    public float ReturnInBoundsYards { get; set; } = 70f;

    /// <summary>Seconds out of bounds before the duel is lost as fled (10 in both references).</summary>
    public int OutOfBoundsGraceSeconds { get; set; } = 10;

    /// <summary>
    /// vmangos permits a duel when a player's area has no AreaTable row (SpellEffects.cpp:4689-4701 tests the flag only when the row exists).
    /// On, an unknown area refuses with SPELL_FAILED_NO_DUELING.
    /// </summary>
    public bool RequireKnownArea { get; set; }

    /// <summary>
    /// Both references end an unaccepted request whose flag object expired as FLED (Player.cpp:6677-6683), which announces a winner.
    /// On, that case completes as INTERRUPTED instead (no winner message).
    /// </summary>
    public bool ExpiredRequestIsSilent { get; set; }
}

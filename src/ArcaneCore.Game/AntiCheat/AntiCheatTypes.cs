namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// What a detector saw (the MaNGOS Zero anticheat fork's AntiCheatViolationType, src/game/AntiCheat/AntiCheatDefines.h,
/// without the bot heuristic). The numbers are stored in <c>character_anticheat_log.type</c>: never renumber them.
/// </summary>
public enum AntiCheatViolation : byte
{
    None = 0,

    /// <summary>Moved farther than the allowed speed over the time the packets cover.</summary>
    Speed = 1,

    /// <summary>One packet moved farther than any speed can explain (blink, teleport hack).</summary>
    Teleport = 2,

    /// <summary>Climbed into the air while claiming to stand on the ground (terrain data required).</summary>
    Vertical = 3,

    /// <summary>A movement flag the server never granted (water walk, hover, slow fall, levitate, fly while swimming, a fake transport).</summary>
    Flag = 4,

    /// <summary>A position the world cannot hold: moving while rooted, walking through a wall, swimming out of water.</summary>
    Physics = 5,

    /// <summary>The client's clock against the server's (a fast clock, oversized or spammed time skips).</summary>
    TimeSync = 6,

    /// <summary>A jump while still in the air (infinite jump).</summary>
    Jump = 7,

    /// <summary>Landing from a damaging height without MSG_MOVE_FALL_LAND (fall damage suppressed).</summary>
    Fall = 8,

    /// <summary>More movement packets per second, by both clocks, than a client sends.</summary>
    Burst = 9,

    /// <summary>A client timestamp that goes backwards or is zero (movement or acknowledgement).</summary>
    PacketTiming = 10,

    /// <summary>A cast of a spell the character does not know (rejected by the spell system).</summary>
    Spell = 11,

    /// <summary>Using an item that is offered in the trade window (rejected; "cheat way only" in vmangos).</summary>
    Item = 12,

    /// <summary>Using a game object from far outside its interaction distance (rejected).</summary>
    Interact = 13,
}

/// <summary>
/// The escalation ladder and the AntiCheat:Action ceiling (the fork's AntiCheatAction): the manager never applies an action
/// above the configured one.
/// </summary>
public enum AntiCheatAction
{
    /// <summary>Detect and score, nothing else (no log row, no line).</summary>
    None = 0,

    /// <summary>Record the violation (log line and violation log row).</summary>
    Log = 1,

    /// <summary>Also notify the staff members online.</summary>
    GmAlert = 2,

    /// <summary>Also move the player back to the last position that passed every check.</summary>
    Rubberband = 3,

    /// <summary>Also disconnect the session (and count the kick towards the account's autoban).</summary>
    Kick = 4,
}

/// <summary>One detector finding: what, how much it weighs on the score, and a fixed text (never client input).</summary>
public readonly record struct AntiCheatFinding(AntiCheatViolation Type, float Weight, string Detail);

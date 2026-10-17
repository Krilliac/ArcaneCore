using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// How a homed bot gets back to its home area (<see cref="PlayerbotHomeOptions.ReturnMode"/>). An ArcaneCore choice: no reference
/// server anchors a bot to an area; the teleport itself is the follower's catch-up teleport (vmangos PartyBotAI <c>HandleGonameCommand</c>,
/// <c>Party.PlayerbotFollower</c>), here to the home's centre.
/// </summary>
public enum PlayerbotHomeReturnMode
{
    /// <summary>Always walk back on the home map (the behaviour before this mode existed); a bot on another map is left alone (off-map).</summary>
    Walk,

    /// <summary>Every return is a teleport to the home's centre, from the home map or another map.</summary>
    Teleport,

    /// <summary>
    /// Walk when the way is short and safe, teleport otherwise (the default): another map, farther than
    /// <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>, a straight line home through an area whose level is more than
    /// <see cref="PlayerbotHomeOptions.DangerLevelMargin"/> above the bot's, or a walk that was set aside as blocked.
    /// </summary>
    Auto,
}

/// <summary>Why a homed bot teleports home instead of walking (logged with each return teleport and shown by inspection).</summary>
public enum PlayerbotHomeTeleportReason
{
    None,

    /// <summary><see cref="PlayerbotHomeReturnMode.Teleport"/>: every return is a teleport.</summary>
    Mode,

    /// <summary>The bot is on another map than its home (another continent, a dungeon it was left in).</summary>
    OtherMap,

    /// <summary>The bot is farther from the centre than <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/>.</summary>
    Distance,

    /// <summary>The straight line home crosses an area more than <see cref="PlayerbotHomeOptions.DangerLevelMargin"/> levels above the bot.</summary>
    Danger,

    /// <summary>The walk home was set aside as blocked (failed plans, no progress, or a stall).</summary>
    WalkBlocked,

    /// <summary>
    /// The spirit healer revived the bot among creatures too strong for it (<c>PlayerbotRecovery.EscapeLanding</c>): the
    /// teleport is made in the think of the resurrection, before they aggro, and the cooldown does not hold it back.
    /// </summary>
    UnsafeLanding,

    /// <summary>
    /// A stall gave the bot's goal up where the navigation mesh joins its ground to nothing round it (PlayerbotNavigation.IsOnMeshIsland,
    /// PlayerbotBrain.EscapeIsland): no walk leaves the place.
    /// </summary>
    Island,
}

/// <summary>
/// <c>World:Playerbots:Home</c>: how a homed bot returns to its home area (docs/areas/playbots.md, Home area). Every key is live:
/// <c>.reload config</c> changes this object in place and a returning bot reads it at its next decision. Not vmangos keys: the home is
/// an ArcaneCore feature.
/// </summary>
public sealed class PlayerbotHomeOptions
{
    /// <summary>Walk, teleport, or teleport only when the walk is long or unsafe (<see cref="PlayerbotHomeReturnMode.Auto"/> by default).</summary>
    public PlayerbotHomeReturnMode ReturnMode { get; set; } = PlayerbotHomeReturnMode.Auto;

    /// <summary>
    /// Auto mode: a bot on the home map farther than this many yards from the centre teleports instead of walking (100..20000). The
    /// default 1500 is twice a 750-yard home (the Sentinel Hill homes of the QA Westfall fleet): the Darkshire road stray stood about 2000
    /// yards from Sentinel Hill.
    /// </summary>
    public float TeleportDistanceYards { get; set; } = 1500f;

    /// <summary>
    /// Auto mode: the straight line from the bot to the centre crossing an area whose AreaTable level (<c>AreaLevel</c>, the 1.12.1
    /// AreaTable.dbc exploration level) is more than this many levels above the bot's counts as unsafe, and the bot teleports (0..60).
    /// </summary>
    public int DangerLevelMargin { get; set; } = 5;

    /// <summary>Throws when a key is out of range (the reload refuses such a value with the same ranges).</summary>
    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Home";
        if (CheckMode(ReturnMode) is { } mode) throw new InvalidOperationException($"{section}: ReturnMode {mode}.");
        if (CheckTeleportDistance(TeleportDistanceYards) is { } distance) throw new InvalidOperationException($"{section}: TeleportDistanceYards {distance}.");
        if (CheckLevelMargin(DangerLevelMargin) is { } margin) throw new InvalidOperationException($"{section}: DangerLevelMargin {margin}.");
    }

    internal static string? CheckMode(PlayerbotHomeReturnMode value) => Enum.IsDefined(value) ? null : "must be Walk, Teleport or Auto";

    internal static string? CheckTeleportDistance(float value) => float.IsFinite(value) && value is >= 100f and <= 20000f ? null : "must be 100..20000";

    internal static string? CheckLevelMargin(int value) => value is >= 0 and <= 60 ? null : "must be 0..60";
}

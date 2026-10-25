using System.Numerics;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Events;

/// <summary>What a festival NPC offers, which decides how many bots may stand around it (<see cref="PlayerbotFestivalCaps"/>).</summary>
internal enum PlayerbotAnchorKind : byte
{
    /// <summary>An NPC that only talks (a Winter Reveler, a Midsummer Celebrant): the larger crowd.</summary>
    Gossip,

    /// <summary>An NPC that gives quests or sells: the smaller crowd, so the NPC stays easy to reach.</summary>
    Service,
}

/// <summary>
/// Everything <see cref="PlayerbotFestivalRules.AnchorKind"/> needs to know about one festival NPC spawn. The defaults are the ones of a
/// spawn that qualifies, so a test names only what it varies.
/// </summary>
internal sealed record PlayerbotAnchorFacts
{
    internal NpcFlags NpcFlags { get; init; }

    /// <summary>The template carries <c>UnitFlags.NotSelectable</c> (a spotlight, a credit marker, a ribbon pole target).</summary>
    internal bool NotSelectable { get; init; }

    internal CreatureBehaviourFlags Behaviour { get; init; }

    /// <summary>The spawn is in the world now (its game event runs: the spawn gate).</summary>
    internal bool Present { get; init; } = true;

    /// <summary>The spawn lies inside the bot's home.</summary>
    internal bool InsideHome { get; init; } = true;

    /// <summary>Yards from the town centre, on the ground plane.</summary>
    internal float DistanceFromTownYards { get; init; }

    /// <summary>The NPC's faction is hostile to the team.</summary>
    internal bool Hostile { get; init; }

    /// <summary>The team's bots may walk up to the NPC (the trainer approach rule).</summary>
    internal bool Approachable { get; init; } = true;

    /// <summary>PvP is enforced on the team where the NPC stands (a contested zone on a PvP realm, enemy territory).</summary>
    internal bool PvpEnforced { get; init; }

    /// <summary>The spawn lies inside an area of a running GM event scenario.</summary>
    internal bool InsideScenarioArea { get; init; }
}

/// <summary>
/// The pure festival rules (docs/areas/playbots-social.md, World events): which festival NPCs are anchors, the spot on the ring around an
/// anchor, the celebrate roll with its staggered day, and the height rule for a spot. No world, no clock; any thread.
/// </summary>
internal static class PlayerbotFestivalRules
{
    /// <summary>A spot is at least this many yards from its anchor: outside the 5 yard NPC interaction distance with room for a body and the NPC's own radius. A constant, not an option: the safety property must not be configurable away.</summary>
    internal const float RingMinYards = 8f;

    /// <summary>A spot is at most this many yards from its anchor.</summary>
    internal const float RingMaxYards = 12f;

    /// <summary>How far above or below a spot's height a bot still counts as being on the spot's floor.</summary>
    internal const float SpotZToleranceYards = 5f;

    private const ulong DaySalt = 0xFE57_1DA7_0000_0001UL;
    private const ulong AngleSalt = 0xFE57_0A91_0000_0002UL;
    private const ulong RadiusSalt = 0xFE57_0AD1_0000_0003UL;
    private const double Unit = 1.0 / 9007199254740992.0;
    private const double SecondsPerDay = 86_400.0;

    /// <summary>
    /// The kind of anchor a festival NPC is, or null when it is none: it talks, gives quests or sells; it is selectable, visible and no
    /// guard; present, inside the home, within <paramref name="radiusYards"/> of the town centre, not hostile, approachable, where PvP is
    /// not enforced on the team and outside every scenario area. A quest giver or vendor (also one that talks) is a
    /// <see cref="PlayerbotAnchorKind.Service"/> anchor.
    /// </summary>
    internal static PlayerbotAnchorKind? AnchorKind(PlayerbotAnchorFacts facts, float radiusYards)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.NotSelectable || (facts.Behaviour & (CreatureBehaviourFlags.Invisible | CreatureBehaviourFlags.Guard)) != 0) return null;
        if (!facts.Present || !facts.InsideHome || facts.Hostile || !facts.Approachable || facts.PvpEnforced || facts.InsideScenarioArea) return null;
        if (!(facts.DistanceFromTownYards <= radiusYards)) return null; // also false for NaN
        if ((facts.NpcFlags & (NpcFlags.QuestGiver | NpcFlags.Vendor)) != 0) return PlayerbotAnchorKind.Service;
        return (facts.NpcFlags & NpcFlags.Gossip) != 0 ? PlayerbotAnchorKind.Gossip : null;
    }

    /// <summary>
    /// The spot of a bot around an anchor: <see cref="RingMinYards"/> to <see cref="RingMaxYards"/> from it on the ground plane, at the
    /// anchor's height, at an angle and distance of the bot's own (a hash of the two guids), so the same bot always picks the same spot
    /// and a crowd spreads around the NPC instead of queueing in one direction.
    /// </summary>
    internal static Vector3 RingPoint(ulong botGuid, ulong anchorGuid, Vector3 anchor)
    {
        ulong mixed = PlayerbotContention.Mix(botGuid, anchorGuid);
        double angle = (PlayerbotContention.Mix(mixed, AngleSalt) >> 11) * Unit * (2.0 * Math.PI);
        double radius = RingMinYards + ((PlayerbotContention.Mix(mixed, RadiusSalt) >> 11) * Unit * (RingMaxYards - RingMinYards));
        return new Vector3(anchor.X + (float)(Math.Cos(angle) * radius), anchor.Y + (float)(Math.Sin(angle) * radius), anchor.Z);
    }

    /// <summary>
    /// The chance (0..1) that a bot of <paramref name="sociability"/> (0..1) celebrates a festival: <c>1 - (1 - c)^(0.5 + sociability)</c> with
    /// <c>c = chancePercent / 100</c>. Exactly 0 at 0 percent and exactly 1 at 100 percent; <c>c</c> at sociability 0.5.
    /// </summary>
    internal static double CelebrateProbability(int chancePercent, double sociability)
    {
        double chance = Math.Clamp(chancePercent, 0, 100) / 100.0;
        return 1.0 - Math.Pow(1.0 - chance, 0.5 + sociability);
    }

    /// <summary>
    /// The bot's roll for a festival on a day, 0 (inclusive) to 1 (exclusive): an order-preserving map of
    /// <c>PlayerbotContention.Mix(Mix(guid, root), day)</c> (never <c>string.GetHashCode</c>, which differs between processes).
    /// </summary>
    internal static double Roll(ulong botGuid, ushort root, long day)
        => (PlayerbotContention.Mix(PlayerbotContention.Mix(botGuid, root), unchecked((ulong)day)) >> 11) * Unit;

    /// <summary>Whether the bot celebrates the festival <paramref name="root"/> on <paramref name="day"/> (<see cref="StaggeredDay"/>).</summary>
    internal static bool Celebrates(ulong botGuid, ushort root, long day, int chancePercent, double sociability)
        => Roll(botGuid, root, day) < CelebrateProbability(chancePercent, sociability);

    /// <summary>
    /// The bot's own UTC day count for <paramref name="unixSeconds"/>: <c>floor((unix + offset) / 86400)</c> with an offset of 0..86399
    /// seconds taken from its guid, so the crowd changes a bot at a time over the day instead of all at midnight.
    /// </summary>
    internal static long StaggeredDay(ulong botGuid, long unixSeconds)
    {
        long offset = (long)(PlayerbotContention.Mix(botGuid, DaySalt) % (ulong)SecondsPerDay);
        return (long)Math.Floor((unixSeconds + offset) / SecondsPerDay);
    }

    /// <summary>
    /// The distance a walk to a spot is judged by: the ground distance when the bot is within <see cref="SpotZToleranceYards"/> of the
    /// spot's height, else that plus the height difference, so a bot on the wrong floor never looks arrived and its walk makes no
    /// progress (the town idle then gives it up).
    /// </summary>
    internal static float SpotDistance(float distance2D, float dz)
    {
        if (float.IsNaN(dz)) return float.PositiveInfinity;
        float height = MathF.Abs(dz);
        return height <= SpotZToleranceYards ? distance2D : distance2D + height;
    }

    /// <summary>The NPC interaction distance: a held bot is never closer than this to its anchor (a stalled walk must not park it in a player's interaction disc).</summary>
    internal const float AnchorClearYards = 5f;

    /// <summary>
    /// How close to its spot a bot counts as arrived (the town idle's linger distance for a festival spot, instead of
    /// <see cref="PlayerbotTownIdle.LingerYards"/>). Derived: every point this near a spot is at least <see cref="AnchorClearYards"/> from the
    /// anchor, since a spot is at least <see cref="RingMinYards"/> from it, so an arrived bot is never inside the interaction disc.
    /// </summary>
    internal const float SpotArriveYards = RingMinYards - AnchorClearYards;

    /// <summary>
    /// Whether a bot <paramref name="distance2D"/> yards (ground plane) and <paramref name="dz"/> from its spot is held there: within the
    /// hold distance, on the spot's floor and at least <see cref="AnchorClearYards"/> (ground plane) from the anchor NPC, always: a stalled
    /// walk that stops inside the interaction disc is given up, and an arrived bot (<see cref="SpotArriveYards"/>) is outside it by construction.
    /// </summary>
    internal static bool IsHeldAtSpot(float distance2D, float dz, float anchorDistance2D)
        => distance2D <= PlayerbotTownIdle.HoldYards && MathF.Abs(dz) <= SpotZToleranceYards && anchorDistance2D >= AnchorClearYards;
}

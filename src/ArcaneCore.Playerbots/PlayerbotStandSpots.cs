using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bot's own stable place on a ring around an anchor (an NPC): a hash of the bot's and the anchor's guid, so the same bot always
/// picks the same spot and a crowd spreads around the anchor instead of standing on one point. Pure: no world, no clock, no random
/// draw (the hash is deterministic, so seeded tests keep their draw order); any thread.
/// </summary>
internal static class PlayerbotStandSpots
{
    /// <summary>
    /// A service NPC's visitor stands at least this far from it on the ground plane: clear of the NPC's own body and the bots that
    /// share the spot's angle at another radius.
    /// </summary>
    internal const float ServiceMinYards = 2f;

    /// <summary>
    /// A service NPC's visitor stands at most this far from it on the ground plane. The server's reach to a creature is
    /// <see cref="ArcaneCore.Game.Npc.QuestNpcServices.InteractionDistance"/> (5 yards, vmangos <c>INTERACTION_DISTANCE</c>) plus both
    /// bounding radii, strict, in 3D (<c>QuestNpcServices.Interactable</c>, vmangos <c>Object::IsWithinDist</c>): 4 yards leaves a yard
    /// for the height difference of a spot and the NPC.
    /// </summary>
    internal const float ServiceMaxYards = 4f;

    /// <summary>The ring spots a visit tries (the bot's own angle, then the same radius at the other angles of <see cref="ServiceAttemptOffsetRadians"/>) before it falls back to the NPC's own position.</summary>
    internal const int ServiceAttempts = 4;

    /// <summary>The angle between two attempts: a quarter turn, so the four attempts cover the whole ring.</summary>
    internal const double ServiceAttemptOffsetRadians = 2.0 * Math.PI / ServiceAttempts;

    private const ulong AngleSalt = 0xFE57_0A91_0000_0002UL;
    private const ulong RadiusSalt = 0xFE57_0AD1_0000_0003UL;
    private const double Unit = 1.0 / 9007199254740992.0;

    /// <summary>
    /// The spot of <paramref name="botGuid"/> around <paramref name="anchor"/>: <paramref name="minYards"/> to <paramref name="maxYards"/>
    /// from it on the ground plane, at the anchor's height, at an angle and distance of the bot's own, the angle turned by
    /// <paramref name="angleOffsetRadians"/> (0 for the bot's own spot).
    /// </summary>
    internal static Vector3 RingPoint(ulong botGuid, ulong anchorGuid, Vector3 anchor, float minYards, float maxYards, double angleOffsetRadians = 0.0)
    {
        ulong mixed = PlayerbotContention.Mix(botGuid, anchorGuid);
        double angle = ((PlayerbotContention.Mix(mixed, AngleSalt) >> 11) * Unit * (2.0 * Math.PI)) + angleOffsetRadians;
        double radius = minYards + ((PlayerbotContention.Mix(mixed, RadiusSalt) >> 11) * Unit * (maxYards - minYards));
        return new Vector3(anchor.X + (float)(Math.Cos(angle) * radius), anchor.Y + (float)(Math.Sin(angle) * radius), anchor.Z);
    }

    /// <summary>The <paramref name="attempt"/>th (0-based, below <see cref="ServiceAttempts"/>) spot of a bot's visit to a service NPC.</summary>
    internal static Vector3 ServiceSpot(ulong botGuid, ulong npcGuid, Vector3 npc, int attempt)
        => RingPoint(botGuid, npcGuid, npc, ServiceMinYards, ServiceMaxYards, attempt * ServiceAttemptOffsetRadians);
}

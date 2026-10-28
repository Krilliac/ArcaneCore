using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>How a bot could reach the place of a group it would advertise for.</summary>
internal enum PlayerbotLfgReach : byte
{
    SameZone,
    AdjacentZone,
    FastTravel,
    Summon,
    TooFar,
}

/// <summary>Whether a bot may advertise for a place, how it would get there, and a one-line trace.</summary>
internal readonly record struct PlayerbotLfgLocalityVerdict(bool MayAdvertise, PlayerbotLfgReach Reach, string Trace);

/// <summary>
/// Decides whether a bot is near enough to a place to put it in an LFG line. A bot advertises a dungeon when it stands in the dungeon's
/// zone or a bordering one, when the planner (<see cref="PlayerbotTravelPlanner"/>: flight paths, the hearthstone and its bind point, mage
/// portals and teleports, ships) puts the trip under <c>LfgMaxTravelSeconds</c>, or when a warlock waiting for the same goal can summon it
/// (Ritual of Summoning, spell 698). Live logs showed a bot advertising Dire Maul (Feralas) in "General - Winterspring".
/// <para>Thread affinity: <c>ForPlace</c> and <see cref="SummonAvailable"/> read world state and run on the world thread; the other members are pure.</para>
/// </summary>
internal static class PlayerbotLfgLocality
{
    /// <summary>Ritual of Summoning (vanilla has one rank).</summary>
    internal const uint RitualOfSummoning = 698;

    // 1.12 AreaTable ids (tests/ThirdParty/WowWorldMessages/src/vanilla/Area.cs, every id checked against it): Kalimdor Teldrassil 141,
    // Darnassus 1657, Darkshore 148, Ashenvale 331, Felwood 361, Winterspring 618, Moonglade 493, Azshara 16, Stonetalon Mountains 406,
    // The Barrens 17, Desolace 405, Feralas 357, Durotar 14, Orgrimmar 1637, Mulgore 215, Thunder Bluff 1638, Thousand Needles 400,
    // Tanaris 440, Un'Goro Crater 490, Silithus 1377; Eastern Kingdoms Elwynn Forest 12, Stormwind City 1519, Westfall 40, Duskwood 10,
    // Redridge Mountains 44, Stranglethorn Vale 33, Deadwind Pass 41, Swamp of Sorrows 8, Blasted Lands 4, Burning Steppes 46, Searing
    // Gorge 51, Blackrock Mountain 25, Badlands 3, Loch Modan 38, Dun Morogh 1, Wetlands 11, Ironforge 1537, Arathi Highlands 45,
    // Hillsbrad Foothills 267, Alterac Mountains 36, Silverpine Forest 130, The Hinterlands 47, Tirisfal Glades 85, Undercity 1497,
    // Western Plaguelands 28, Eastern Plaguelands 139. The pairs are land borders: well-known game geography (what a player sees on the
    // world map), not data from a server. A pair missing here only falls back to the travel-time check of Judge.
    private static readonly (uint A, uint B)[] BorderPairs =
    [
        // Kalimdor
        (141, 1657), (148, 331), (148, 361), (331, 361), (331, 16), (331, 406), (331, 17), (361, 618), (361, 493), (618, 493),
        (406, 17), (406, 405), (405, 357), (17, 14), (17, 215), (17, 15), (17, 400), (14, 1637), (1637, 17), (215, 1638), (400, 357),
        (400, 440), (440, 490), (490, 1377),
        // Eastern Kingdoms (25 is Blackrock Mountain)
        (12, 1519), (12, 40), (12, 10), (12, 44), (40, 10), (10, 44), (10, 33), (10, 41), (44, 46), (41, 8), (8, 4), (46, 51), (46, 25),
        (51, 25), (3, 38), (38, 1), (38, 11), (1, 1537), (11, 45), (45, 267), (267, 36), (267, 130), (267, 47), (130, 85), (85, 1497),
        (85, 28), (28, 139),
    ];

    private static readonly HashSet<ulong> Borders = BuildBorders();

    private static HashSet<ulong> BuildBorders()
    {
        var set = new HashSet<ulong>(BorderPairs.Length * 2);
        foreach ((uint a, uint b) in BorderPairs)
        {
            set.Add(Pack(a, b));
            set.Add(Pack(b, a));
        }

        return set;
    }

    private static ulong Pack(uint a, uint b) => ((ulong)a << 32) | b;

    /// <summary>Whether two zones border each other (symmetric; false for a zone with itself and for the unknown zone 0).</summary>
    internal static bool Adjacent(uint zoneA, uint zoneB) => zoneA != 0 && zoneB != 0 && Borders.Contains(Pack(zoneA, zoneB));

    /// <summary>Judges the bot's position against the place; <paramref name="travelSeconds"/> is the planned trip, null when none exists.</summary>
    internal static PlayerbotLfgLocalityVerdict Judge(uint botMap, uint botZone, uint placeMap, uint placeZone, float? travelSeconds, bool summonAvailable, int maxTravelSeconds)
    {
        if (botMap == placeMap && botZone != 0)
        {
            if (botZone == placeZone)
                return new PlayerbotLfgLocalityVerdict(true, PlayerbotLfgReach.SameZone, string.Create(CultureInfo.InvariantCulture, $"same zone {botZone}"));
            if (Adjacent(botZone, placeZone))
                return new PlayerbotLfgLocalityVerdict(true, PlayerbotLfgReach.AdjacentZone, string.Create(CultureInfo.InvariantCulture, $"zone {botZone} borders {placeZone}"));
        }

        if (travelSeconds is { } seconds && seconds <= maxTravelSeconds)
            return new PlayerbotLfgLocalityVerdict(true, PlayerbotLfgReach.FastTravel, string.Create(CultureInfo.InvariantCulture, $"travel {seconds:0} s <= {maxTravelSeconds} s"));
        if (summonAvailable)
            return new PlayerbotLfgLocalityVerdict(true, PlayerbotLfgReach.Summon, "a warlock of the group can summon");
        string route = travelSeconds is { } far
            ? string.Create(CultureInfo.InvariantCulture, $"travel {far:0} s > {maxTravelSeconds} s")
            : "no route";
        return new PlayerbotLfgLocalityVerdict(false, PlayerbotLfgReach.TooFar, string.Create(CultureInfo.InvariantCulture, $"zone {botZone} -> {placeZone}: {route}"));
    }

    /// <summary>Seconds of the fastest planned trip from <paramref name="from"/> to <paramref name="to"/>, or null when there is none.</summary>
    internal static float? TravelSeconds(TravelFacts facts, TravelPlace from, TravelPlace to) => PlayerbotTravelPlanner.Plan(facts, from, to)?.Seconds;

    /// <summary>
    /// The zone at <paramref name="place"/> on <paramref name="placeMap"/> as <paramref name="viewer"/>'s map knows it without reading a
    /// terrain file; 0 when the viewer is on another map, the tile is not in memory, or the position is in no zone.
    /// </summary>
    internal static uint ZoneAt(Player viewer, uint placeMap, Vector3 place)
        => viewer.MapId == placeMap && viewer.Map is { } map && map.TryGetZoneAndAreaIdWithoutLoading(place.X, place.Y, place.Z, out (uint ZoneId, uint AreaId) found)
            ? found.ZoneId
            : 0;

    /// <summary>
    /// Whether <paramref name="player"/> may advertise the place (<paramref name="placeMap"/>, <paramref name="place"/>): the zones first, then the
    /// planner's trip for the bot's own travel options (<see cref="PlayerbotTravel.BuildFacts(WorldSession, Player, PlayerbotOptions)"/>), then
    /// <paramref name="summonAvailable"/>. A planner failure counts as no route. World thread; the planner runs only when the zones do not decide.
    /// </summary>
    internal static PlayerbotLfgLocalityVerdict ForPlace(WorldSession session, Player player, PlayerbotOptions options, uint placeMap, Vector3 place, bool summonAvailable)
        => ForPlace(session, player, options, placeMap, place, () => summonAvailable);

    /// <summary>
    /// <see cref="ForPlace(WorldSession, Player, PlayerbotOptions, uint, Vector3, bool)"/> with the summon worked out lazily:
    /// <paramref name="summonAvailable"/> runs only when neither the zones nor the planned trip allow advertising (null: no summon).
    /// </summary>
    internal static PlayerbotLfgLocalityVerdict ForPlace(WorldSession session, Player player, PlayerbotOptions options, uint placeMap, Vector3 place, Func<bool>? summonAvailable)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(options);
        int max = options.Chat.Voice.LfgMaxTravelSeconds;
        uint placeZone = ZoneAt(player, placeMap, place);
        PlayerbotLfgLocalityVerdict byZone = Judge(player.MapId, player.ZoneId, placeMap, placeZone, null, false, max);
        if (byZone.MayAdvertise) return byZone;

        float? seconds = null;
        try
        {
            seconds = TravelSeconds(PlayerbotTravel.BuildFacts(session, player, options), TravelPlace.Of(player), new TravelPlace(placeMap, place));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("playerbot.lfg-locality", ex);
        }

        PlayerbotLfgLocalityVerdict byTrip = Judge(player.MapId, player.ZoneId, placeMap, placeZone, seconds, false, max);
        if (byTrip.MayAdvertise || summonAvailable is null || !summonAvailable()) return byTrip;
        return Judge(player.MapId, player.ZoneId, placeMap, placeZone, seconds, true, max);
    }

    /// <summary>The members at the place the 1.12 ritual needs: the warlock who casts it and two more who click the portal.</summary>
    internal const int SummonersNeeded = 3;

    /// <summary>
    /// Whether the bots of <paramref name="group"/> other than <paramref name="asker"/> can summon it: at least <see cref="SummonersNeeded"/>
    /// of them are alive and stand in the zone of the place or a bordering one, and one of those is a warlock who knows Ritual of Summoning
    /// (<see cref="RitualOfSummoning"/>) and carries a Soul Shard (<see cref="SoulShardRules.SoulShard"/>). World thread.
    /// </summary>
    internal static bool SummonAvailable(IEnumerable<(Player Player, WorldSession Session)> group, Player asker, uint placeMap, Vector3 place)
    {
        ArgumentNullException.ThrowIfNull(group);
        int local = 0;
        bool warlock = false;
        foreach ((Player other, WorldSession session) in group)
        {
            if (ReferenceEquals(other, asker) || !other.IsAlive) continue;
            PlayerbotLfgReach reach = Judge(other.MapId, other.ZoneId, placeMap, ZoneAt(other, placeMap, place), null, false, 0).Reach;
            if (reach is not (PlayerbotLfgReach.SameZone or PlayerbotLfgReach.AdjacentZone)) continue;
            local++;
            if (!warlock && other.Class == Class.Warlock && other.Inventory.GetItemCount(SoulShardRules.SoulShard) > 0
                && session.Services.GetService<SpellFeature>() is { } spells && spells.Spellbook.HasSpell(other, RitualOfSummoning))
                warlock = true;
        }

        return warlock && local >= SummonersNeeded;
    }
}

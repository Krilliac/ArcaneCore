using System.Globalization;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>Why a bot would run a dungeon at all; any one reason can justify a run that the level band alone would not.</summary>
[Flags]
internal enum PlayerbotDungeonReasons : byte
{
    None = 0,
    Quest = 1,
    Attunement = 2,
    GearUpgrade = 4,
    Companion = 8,
    FormingGroup = 16,
    Farming = 32,
}

/// <summary>An inclusive level range an instance is run at (the 1.12 recommended levels).</summary>
internal readonly record struct PlayerbotDungeonBand(byte Min, byte Max)
{
    /// <summary>Whether <paramref name="level"/> lies inside the range.</summary>
    public bool Holds(int level) => level >= Min && level <= Max;

    /// <summary>How many levels <paramref name="level"/> is outside the range (0 inside it).</summary>
    public int DistanceFrom(int level) => level < Min ? Min - level : level > Max ? level - Max : 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Min}-{Max}");
}

/// <summary>The outcome of judging a bot against a dungeon: whether it may go, whether its level is in the band, why, and a one-line trace.</summary>
internal readonly record struct PlayerbotDungeonVerdict(bool Allowed, bool InBand, PlayerbotDungeonReasons Reasons, string Trace);

/// <summary>
/// Decides whether a bot of a given level should run a dungeon, and which of several it should pick. A bot inside a dungeon's level band
/// goes without a reason; outside it, only with one (a quest that is not grey for it, an attunement step, a gear upgrade there, a companion
/// or a forming group, farming). Live logs showed level 58-60 bots queueing ~1300 Wailing Caverns groups for a level-21 quest.
/// <para>Thread affinity: pure functions, any thread.</para>
/// </summary>
internal static class PlayerbotDungeonFit
{
    /// <summary>Levels of slack allowed either side of a band.</summary>
    internal const int StaticSlack = 2;

    /// <summary>The reasons that stand on their own; Companion and FormingGroup are derived from other bots waiting and anchor nothing.</summary>
    internal const PlayerbotDungeonReasons DirectReasons = PlayerbotDungeonReasons.Quest | PlayerbotDungeonReasons.Attunement | PlayerbotDungeonReasons.GearUpgrade;

    /// <summary>The band of the instance on <paramref name="mapId"/>, from its entrance level and the catalogue bands sharing the map.</summary>
    internal static PlayerbotDungeonBand BandOf(uint mapId, byte entranceLevel, IEnumerable<PlayerbotDungeonBand> catalogueBands)
    {
        ArgumentNullException.ThrowIfNull(catalogueBands);
        bool any = false;
        byte min = byte.MaxValue, max = 0;
        foreach (PlayerbotDungeonBand band in catalogueBands)
        {
            any = true;
            if (band.Min < min) min = band.Min;
            if (band.Max > max) max = band.Max;
        }

        if (any) return new PlayerbotDungeonBand(min, max);
        if (ArcaneCore.World.Playerbots.Chat.PlayerbotDungeons.RecommendedBand(mapId) is { } recommended)
            return new PlayerbotDungeonBand((byte)Math.Clamp(recommended.Min - StaticSlack, 1, 255), (byte)Math.Clamp(recommended.Max + StaticSlack, 1, 255));
        return new PlayerbotDungeonBand(Math.Max(entranceLevel, (byte)1), 255);
    }

    /// <summary>Whether a quest of <paramref name="questLevel"/> justifies a run for a bot of <paramref name="level"/>: it is not grey for the bot.</summary>
    internal static bool QuestQualifies(int level, int questLevel)
        => questLevel > ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel((uint)Math.Max(level, 0));

    /// <summary>Judges a bot of <paramref name="level"/> against <paramref name="band"/> given the reasons it has to go.</summary>
    internal static PlayerbotDungeonVerdict Judge(int level, PlayerbotDungeonBand band, PlayerbotDungeonReasons reasons, bool reasonRequired = true)
    {
        if (band.Holds(level))
            return new PlayerbotDungeonVerdict(true, true, reasons, string.Create(CultureInfo.InvariantCulture, $"in-band {level} in {band}"));
        if (reasons != PlayerbotDungeonReasons.None || !reasonRequired)
            return new PlayerbotDungeonVerdict(true, false, reasons, string.Create(CultureInfo.InvariantCulture, $"out-of-band {level} vs {band}: {Describe(reasons)}"));
        return new PlayerbotDungeonVerdict(false, false, reasons, string.Create(CultureInfo.InvariantCulture, $"{level} is outside {band} and has no reason"));
    }

    /// <summary>
    /// The index of the best option the bot may take, or null for none: an in-band option first (the highest band minimum wins), else an
    /// out-of-band one allowed by <see cref="Judge"/> with the smallest distance from its band; ties keep the lower index.
    /// </summary>
    internal static int? Choose(int level, IReadOnlyList<(PlayerbotDungeonBand Band, PlayerbotDungeonReasons Reasons)> options, bool reasonRequired = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        int? best = null;
        bool bestInBand = false;
        int bestKey = 0; // in band: the band minimum (higher is better); out of band: the distance (lower is better)
        for (int i = 0; i < options.Count; i++)
        {
            (PlayerbotDungeonBand band, PlayerbotDungeonReasons reasons) = options[i];
            if (band.Holds(level))
            {
                if (best is null || !bestInBand || band.Min > bestKey)
                {
                    best = i;
                    bestInBand = true;
                    bestKey = band.Min;
                }
            }
            else if (!bestInBand && (reasons != PlayerbotDungeonReasons.None || !reasonRequired))
            {
                int distance = band.DistanceFrom(level);
                if (best is null || distance < bestKey)
                {
                    best = i;
                    bestKey = distance;
                }
            }
        }

        return best;
    }

    /// <summary>A short text for <paramref name="reasons"/>: the lowercase flag names joined with "+" ("quest+attunement"), "none" for none.</summary>
    internal static string Describe(PlayerbotDungeonReasons reasons)
        => reasons == PlayerbotDungeonReasons.None ? "none" : reasons.ToString().Replace(", ", "+", StringComparison.Ordinal).ToLowerInvariant();
}

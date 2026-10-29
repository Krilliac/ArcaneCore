using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>The valid (race, class) pairs, filled once from the world data store (the same check CMSG_CHAR_CREATE uses).</summary>
internal sealed class PopulationRaceClassCache
{
    /// <summary>The playable 1.12 races: 1 human, 2 orc, 3 dwarf, 4 night elf, 5 undead, 6 tauren, 7 gnome, 8 troll.</summary>
    public static readonly IReadOnlyList<byte> Races = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly HashSet<(byte Race, byte Class)> _valid;
    private readonly IReadOnlyDictionary<byte, uint> _raceFactions;

    public PopulationRaceClassCache(IEnumerable<(byte Race, byte Class)> valid, IReadOnlyDictionary<byte, uint>? raceFactions = null)
    {
        _valid = [.. valid];
        _raceFactions = raceFactions is null ? new Dictionary<byte, uint>() : new Dictionary<byte, uint>(raceFactions);
    }

    /// <summary>The key a cache was built for (the class list, in order).</summary>
    public string Key { get; init; } = string.Empty;

    public int Count => _valid.Count;

    /// <summary>The faction template the ordinary character-create data gives this race (0 when the source is incomplete).</summary>
    public uint FactionTemplateOf(byte race) => _raceFactions.GetValueOrDefault(race);

    /// <summary>Alliance races are human, dwarf, night elf and gnome; the others are Horde (<see cref="Player.Team"/>).</summary>
    public static Team TeamOf(byte race) => TeamRules.TeamOf((Race)race);

    public static string KeyOf(IEnumerable<uint> classes) => string.Join(',', classes);

    /// <summary>Asks <see cref="IWorldDataStore.IsValidRaceClassAsync"/> for every race and each class (once; call off the world thread).</summary>
    public static async Task<PopulationRaceClassCache> BuildAsync(IWorldDataStore store, IReadOnlyCollection<uint> classes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var valid = new List<(byte, byte)>();
        var factions = new Dictionary<byte, uint>();
        foreach (byte race in Races)
            if (await store.GetRaceInfoAsync(race, 0, cancellationToken).ConfigureAwait(false) is { } info)
                factions[race] = info.FactionTemplate;
        foreach (uint cls in classes)
            foreach (byte race in Races)
                if (await store.IsValidRaceClassAsync(race, (byte)cls, cancellationToken).ConfigureAwait(false)) valid.Add((race, (byte)cls));
        return new PopulationRaceClassCache(valid, factions) { Key = KeyOf(classes) };
    }

    /// <summary>The classes that have at least one valid race of the team, among <paramref name="classes"/>.</summary>
    public IReadOnlyList<byte> ClassesFor(Team team, IEnumerable<uint> classes)
        => [.. classes.Select(c => (byte)c).Where(c => Races.Any(r => TeamOf(r) == team && _valid.Contains((r, c))))];

    public IReadOnlyList<byte> RacesFor(Team team, byte cls) => [.. Races.Where(r => TeamOf(r) == team && _valid.Contains((r, cls)))];
}

/// <summary>A planned bot: the band it fills, its team, race, class, and the level range it may have.</summary>
internal sealed record PopulationPlan(int Band, Team Team, byte Race, byte Class, PopulationPlacement Placement, int Level);

/// <summary>A fresh-launch bot: only its team, race and class (it stays level 1 where its race starts).</summary>
internal sealed record PopulationFreshPlan(Team Team, byte Race, byte Class);

/// <summary>
/// Pure planning of the bots the population creates: how many each band should hold, which band is furthest below its share, which
/// faction, race and class the next bot gets, and a level that fits both the band and the zone it is placed in.
/// </summary>
internal static class PopulationBandPlanner
{
    /// <summary>
    /// The bots each band should hold: <c>target * weight / sum of weights</c> by largest remainder (the floors, then one more to the
    /// bands with the largest remainders, the lower band first on ties), so the counts add up to the target.
    /// </summary>
    public static int[] DesiredPerBand(int target, IReadOnlyList<PopulationBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var desired = new int[bands.Count];
        long total = bands.Sum(b => (long)Math.Max(0, b.Weight));
        if (total == 0 || target <= 0) return desired;
        var remainders = new long[bands.Count];
        int given = 0;
        for (int i = 0; i < bands.Count; i++)
        {
            long product = (long)target * Math.Max(0, bands[i].Weight);
            desired[i] = (int)(product / total);
            remainders[i] = product % total;
            given += desired[i];
        }

        foreach (int i in Enumerable.Range(0, bands.Count).Where(i => bands[i].Weight > 0).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take(target - given))
            desired[i]++;
        return desired;
    }

    /// <summary>The bands' deficits: desired minus the running bots in the band, never below 0.</summary>
    public static int[] Deficits(int[] desired, int[] have)
    {
        var deficit = new int[desired.Length];
        for (int i = 0; i < desired.Length; i++) deficit[i] = Math.Max(0, desired[i] - have[i]);
        return deficit;
    }

    /// <summary>The eligible band with the largest deficit (the lowest band on ties), or -1 when no eligible band has a deficit.</summary>
    public static int NextBand(int[] desired, int[] have, Func<int, bool>? eligible = null)
    {
        int[] deficit = Deficits(desired, have);
        int best = -1;
        for (int i = 0; i < deficit.Length; i++)
        {
            if (deficit[i] == 0 || eligible?.Invoke(i) == false) continue;
            if (best < 0 || deficit[i] > deficit[best]) best = i;
        }

        return best;
    }

    /// <summary>
    /// The faction of the next bot. One faction configured: that one. Both: whichever is furthest below its share of the bots running
    /// after this one is added (Alliance on a tie), so the split follows <c>AllianceSharePct</c> from the first bot on.
    /// </summary>
    public static Team ChooseTeam(PlayerbotPopulationOptions options, int alliance, int horde)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Factions)
        {
            case PopulationFactions.Alliance: return Team.Alliance;
            case PopulationFactions.Horde: return Team.Horde;
        }

        double share = options.AllianceSharePct / 100.0;
        double total = alliance + horde + 1;
        double allianceNeed = share * total - alliance;
        double hordeNeed = (1 - share) * total - horde;
        return allianceNeed >= hordeNeed ? Team.Alliance : Team.Horde;
    }

    /// <summary>A race and class for the team: a class (of the configured ones) that has a valid race of the team, then one of its races, both uniformly. Null when there is none.</summary>
    public static (byte Race, byte Class)? ChooseRaceClass(PopulationRaceClassCache cache, Team team, IReadOnlyCollection<uint> classes, Random random)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(random);
        IReadOnlyList<byte> usable = cache.ClassesFor(team, classes);
        if (usable.Count == 0) return null;
        byte cls = usable[random.Next(usable.Count)];
        IReadOnlyList<byte> races = cache.RacesFor(team, cls);
        return (races[random.Next(races.Count)], cls);
    }

    /// <summary>The index of the band that holds <paramref name="level"/>, or -1.</summary>
    public static int BandOf(IReadOnlyList<PopulationBand> bands, int level)
    {
        for (int i = 0; i < bands.Count; i++)
            if (bands[i].Contains(level)) return i;
        return -1;
    }

    /// <summary>
    /// Fresh launch: the faction furthest below its share (<see cref="ChooseTeam"/>), then a class and one of its races of that team
    /// (<see cref="ChooseRaceClass"/>). No band, zone or level: the bot is level 1 at its race start. Null when no race and class fits.
    /// </summary>
    public static PopulationFreshPlan? PlanFresh(PlayerbotPopulationOptions options, int alliance, int horde, PopulationRaceClassCache cache, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        Team team = ChooseTeam(options, alliance, horde);
        return ChooseRaceClass(cache, team, options.Classes, random) is { } pair ? new PopulationFreshPlan(team, pair.Race, pair.Class) : null;
    }

    /// <summary>
    /// Fresh launch with the start-zone cap (<see cref="PlayerbotPopulationOptions.FreshStartZoneCap"/>). The faction is chosen as in
    /// <see cref="PlanFresh(PlayerbotPopulationOptions, int, int, PopulationRaceClassCache, Random)"/>, then the classes of the faction
    /// in random order; the first class with a race whose start area holds fewer than <paramref name="cap"/> bots wins, and of its
    /// open races the one in the least crowded area (ties at random), which spreads the bots over the six start areas. When the
    /// preferred faction has no open area and both factions are configured, the other faction is tried (the share is kept whenever a
    /// start area is open). <paramref name="startCounts"/> maps an area key (<see cref="PopulationStartAreas.Areas"/>) to its bots.
    /// Null with <paramref name="full"/> false: no valid race and class; null with <paramref name="full"/> true: valid ones exist but
    /// every start area they could use is at the cap (creation is deferred, not failed). A cap of 0 is no cap.
    /// </summary>
    public static PopulationFreshPlan? PlanFresh(PlayerbotPopulationOptions options, int alliance, int horde, PopulationRaceClassCache cache, Random random,
        IReadOnlyDictionary<string, int> startCounts, int cap, out bool full)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(startCounts);
        full = false;
        if (cap <= 0) return PlanFresh(options, alliance, horde, cache, random);
        Team preferred = ChooseTeam(options, alliance, horde);
        Team[] teams = options.Factions == PopulationFactions.Both ? [preferred, preferred == Team.Alliance ? Team.Horde : Team.Alliance] : [preferred];
        bool anyValid = false;
        foreach (Team team in teams)
        {
            byte[] classes = [.. cache.ClassesFor(team, options.Classes)];
            for (int i = classes.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (classes[i], classes[j]) = (classes[j], classes[i]);
            }

            foreach (byte cls in classes)
            {
                IReadOnlyList<byte> races = cache.RacesFor(team, cls);
                if (races.Count > 0) anyValid = true;
                int least = int.MaxValue;
                var open = new List<byte>();
                foreach (byte race in races)
                {
                    int held = PopulationStartAreas.AreaOf(race) is { } area ? startCounts.GetValueOrDefault(area) : 0;
                    if (held >= cap) continue;
                    if (held < least) { least = held; open.Clear(); }
                    if (held == least) open.Add(race);
                }

                if (open.Count > 0) return new PopulationFreshPlan(team, open[random.Next(open.Count)], cls);
            }
        }

        full = anyValid;
        return null;
    }

    /// <summary>
    /// Plans one new bot, or returns null when nothing fits (no band with a deficit that a zone takes, no race and class). Without the
    /// boost only level 1 can be given, so only bands containing level 1 are eligible and the level is 1.
    /// </summary>
    public static PopulationPlan? Plan(PlayerbotPopulationOptions options, int[] desired, int[] have, int alliance, int horde,
        PopulationRaceClassCache cache, PopulationZoneAtlas atlas, IReadOnlyDictionary<uint, int> assigned, bool boostAvailable, Random random)
    {
        ArgumentNullException.ThrowIfNull(options);
        PopulationBand[] bands = options.Bands;
        PopulationLevelRule rule = PopulationLevelRule.From(options);
        Team team = ChooseTeam(options, alliance, horde);
        (byte Race, byte Class)? raceClass = ChooseRaceClass(cache, team, options.Classes, random);
        if (raceClass is null) return null;

        // The bands with a deficit, largest first; the first one a zone can take wins.
        int[] deficit = Deficits(desired, have);
        foreach (int band in Enumerable.Range(0, bands.Length).Where(i => deficit[i] > 0 && (boostAvailable || bands[i].MinLevel <= 1))
            .OrderByDescending(i => deficit[i]).ThenBy(i => i))
        {
            int minLevel = boostAvailable ? bands[band].MinLevel : 1;
            int maxLevel = boostAvailable ? bands[band].MaxLevel : 1;
            PopulationPlacement? placement = atlas.Pick(minLevel, maxLevel, team, rule, assigned, random);
            if (placement is null) continue;
            int level = random.Next(placement.LowLevel, placement.HighLevel + 1);
            return new PopulationPlan(band, team, raceClass.Value.Race, raceClass.Value.Class, placement, level);
        }

        return null;
    }

    /// <summary>
    /// World-thread planner: every level and location in a fitting zone is tested before creation. An unsafe least-assigned
    /// zone never monopolizes the next attempt; when both teams are enabled, the other team is considered as well. With a zone
    /// <paramref name="value"/> (and its <paramref name="weights"/>), a band at the level cap tries the valued zones before the others.
    /// </summary>
    internal static PopulationPlan? PlanSafe(PlayerbotPopulationOptions options, int[] desired, int[] have, int alliance, int horde,
        PopulationRaceClassCache cache, PopulationZoneAtlas atlas, IReadOnlyDictionary<uint, int> assigned, bool boostAvailable,
        Random random, Func<WorldSafeLoc, byte, uint, bool> safe, PopulationZoneValue? value = null, PopulationValueWeights? weights = null,
        Func<uint, int>? trouble = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(safe);
        PopulationLevelRule rule = PopulationLevelRule.From(options);
        Team preferred = ChooseTeam(options, alliance, horde);
        Team[] teams = options.Factions == PopulationFactions.Both
            ? [preferred, preferred == Team.Alliance ? Team.Horde : Team.Alliance] : [preferred];
        int[] deficits = Deficits(desired, have);
        // (band, team, faction) triples with no safe placement in this call: the pick depends on nothing else about the race or
        // class, so a second race of the same faction, or another class, would only repeat the same failed search. The team is
        // part of the key because an incomplete catalog gives faction 0 to races of both teams.
        var unplaceable = new HashSet<(int Band, Team Team, uint Faction)>();
        foreach (Team team in teams)
            foreach (int band in Enumerable.Range(0, options.Bands.Length)
                .Where(i => deficits[i] > 0 && (boostAvailable || options.Bands[i].MinLevel <= 1))
                .OrderByDescending(i => deficits[i]).ThenBy(i => i))
            {
                IReadOnlyList<byte> classes = cache.ClassesFor(team, options.Classes);
                if (classes.Count == 0) continue;
                int firstClass = random.Next(classes.Count);
                for (int ci = 0; ci < classes.Count; ci++)
                {
                    byte cls = classes[(firstClass + ci) % classes.Count];
                    IReadOnlyList<byte> races = cache.RacesFor(team, cls);
                    int firstRace = random.Next(races.Count);
                    for (int ri = 0; ri < races.Count; ri++)
                    {
                        byte race = races[(firstRace + ri) % races.Count];
                        uint faction = cache.FactionTemplateOf(race);
                        if (unplaceable.Contains((band, team, faction))) continue;
                        int minLevel = boostAvailable ? options.Bands[band].MinLevel : 1;
                        int maxLevel = boostAvailable ? options.Bands[band].MaxLevel : 1;
                        // A band at the level cap has no zone that fits it for long: send it to the zones with the most to offer first.
                        PopulationSafeSelection? selected = value is not null && weights is not null && boostAvailable
                            && options.Bands[band].MinLevel >= PlayerbotPopulationOptions.MaxLevel
                            ? atlas.PickValued(PopulationBotProfile.Fresh(minLevel, team, race, cls), rule, value, weights, assigned, random,
                                (location, level) => safe(location, checked((byte)level), faction), trouble: trouble)
                            : null;
                        selected ??= atlas.PickSafe(minLevel, maxLevel, team, rule,
                            assigned, random, (location, level) => safe(location, checked((byte)level), faction));
                        if (selected is not null)
                            return new PopulationPlan(band, team, race, cls, selected.Placement, selected.Level);
                        unplaceable.Add((band, team, faction));
                    }
                }
            }

        return null;
    }
}

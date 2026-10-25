namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// Zones a bot escaped from (a dead-end pin or an island: <c>PlayerbotBrain</c> teleported it out) and must not be relocated back
/// into for a while. An escape lasts <see cref="BaseTtlSeconds"/> the first time and doubles with each repeat for the same bot and
/// zone, up to <see cref="MaxTtlSeconds"/>. The escape teleports the bot to its bind point, usually a low zone, and without the veto
/// the next relocation pass would count it out-levelled there and put it straight back in the zone it escaped.
/// <para>Thread affinity: world thread only (the feature records on its capture and reads in its relocation pass). Not persisted: a restart forgets the vetoes; strikes are forgotten ForgetAfterSeconds after a veto ends.</para>
/// </summary>
internal sealed class PopulationZoneVetoes
{
    public const long BaseTtlSeconds = 60 * 60;
    public const long MaxTtlSeconds = 8 * 60 * 60;

    /// <summary>Strikes are forgotten (the entry is dropped) once this long has passed since its veto ended.</summary>
    public const long ForgetAfterSeconds = 24 * 60 * 60;

    private readonly Dictionary<(Guid BotId, uint ZoneId), (long UntilUnix, int Strikes)> _entries = [];

    /// <summary>Entries held (diagnostics and tests).</summary>
    public int Count => _entries.Count;

    /// <summary>The seconds a veto lasts at the given number of strikes (1 = first): 60 min, doubling, at most 8 h.</summary>
    public static long TtlOf(int strikes)
        => strikes <= 1 ? BaseTtlSeconds : strikes >= 5 ? MaxTtlSeconds : Math.Min(MaxTtlSeconds, BaseTtlSeconds << (strikes - 1));

    /// <summary>Record that the bot escaped from the zone at <paramref name="nowUnix"/>; the veto runs from now for the TTL of its strike count.</summary>
    public void RecordEscape(Guid botId, uint zoneId, long nowUnix)
    {
        if (zoneId == 0) return;
        foreach ((Guid BotId, uint ZoneId) stale in _entries.Where(e => nowUnix - e.Value.UntilUnix > ForgetAfterSeconds).Select(e => e.Key).ToArray())
            _entries.Remove(stale);
        int strikes = _entries.TryGetValue((botId, zoneId), out var existing) ? existing.Strikes + 1 : 1;
        _entries[(botId, zoneId)] = (nowUnix + TtlOf(strikes), strikes);
    }

    /// <summary>Whether the bot may not be relocated into the zone at <paramref name="nowUnix"/>.</summary>
    public bool IsVetoed(Guid botId, uint zoneId, long nowUnix)
        => _entries.TryGetValue((botId, zoneId), out var entry) && nowUnix < entry.UntilUnix;
}

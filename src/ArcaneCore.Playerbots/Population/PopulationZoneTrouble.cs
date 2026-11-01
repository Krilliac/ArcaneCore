namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// How much trouble bots had in each zone lately: a bot found stalled or dead there is recorded once per zone and
/// <see cref="RecordIntervalSeconds"/>, and <see cref="Count"/> gives the records of the last <see cref="WindowSeconds"/>. The zone value
/// scores a troublesome zone down. Not persisted: a restart forgets it.
/// <para>Thread affinity: world thread only (the feature records on its capture and reads in its relocation pass).</para>
/// </summary>
internal sealed class PopulationZoneTrouble
{
    /// <summary>The same bot in the same zone is recorded again only after this long.</summary>
    public const long RecordIntervalSeconds = 10 * 60;

    /// <summary>How far back <see cref="Count"/> looks.</summary>
    public const long WindowSeconds = 60 * 60;

    private readonly Dictionary<(uint ZoneId, Guid BotId), long> _lastRecorded = [];
    private readonly Dictionary<uint, List<long>> _records = [];

    /// <summary>Records that the bot was stalled or dead in the zone at <paramref name="nowUnix"/>; ignored for zone 0 or when it was recorded less than <see cref="RecordIntervalSeconds"/> ago.</summary>
    public void Record(uint zoneId, Guid botId, long nowUnix)
    {
        if (zoneId == 0) return;
        if (_lastRecorded.TryGetValue((zoneId, botId), out long last) && nowUnix - last < RecordIntervalSeconds) return;
        _lastRecorded[(zoneId, botId)] = nowUnix;
        if (!_records.TryGetValue(zoneId, out List<long>? list)) _records[zoneId] = list = [];
        list.Add(nowUnix);
    }

    /// <summary>The records of the zone in the last <see cref="WindowSeconds"/> up to <paramref name="nowUnix"/>.</summary>
    public int Count(uint zoneId, long nowUnix)
    {
        if (!_records.TryGetValue(zoneId, out List<long>? list)) return 0;
        int count = 0;
        for (int i = list.Count - 1; i >= 0 && nowUnix - list[i] < WindowSeconds; i--) count++;
        return count;
    }

    /// <summary>Forgets what is older than the window (call once per pass).</summary>
    public void Prune(long nowUnix)
    {
        foreach (uint zone in _records.Keys.ToArray())
        {
            List<long> list = _records[zone];
            list.RemoveAll(t => nowUnix - t >= WindowSeconds);
            if (list.Count == 0) _records.Remove(zone);
        }

        foreach ((uint ZoneId, Guid BotId) key in _lastRecorded.Where(p => nowUnix - p.Value >= WindowSeconds).Select(p => p.Key).ToArray())
            _lastRecorded.Remove(key);
    }
}

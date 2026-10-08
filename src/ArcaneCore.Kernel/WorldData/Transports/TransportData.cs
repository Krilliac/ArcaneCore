namespace ArcaneCore.Kernel.WorldData.Transports;

/// <summary>
/// One <c>transports</c> row (vmangos world database, sql/migrations/20250530110153_world.sql): the measured round-trip time of
/// a ship route for the client builds from <see cref="Build"/> on. The server computes a period from the path; vmangos replaces
/// it with this value ("our algorithm is not perfect", TransportMgr.cpp:62-80).
/// </summary>
public sealed record TransportPeriodRow(uint Entry, ushort Build, string Name, uint Period);

/// <summary>Reads the <c>transports</c> rows (world database).</summary>
public interface ITransportDataStore
{
    Task<IReadOnlyList<TransportPeriodRow>> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>The period overrides the 1.12.1 server uses.</summary>
public static class TransportPeriods
{
    /// <summary>
    /// vmangos <c>LoadTransportTemplates</c>: per entry, the row of the highest build at or below <paramref name="clientBuild"/>
    /// (<c>WHERE build = (SELECT max(build) ... AND build &lt;= SUPPORTED_CLIENT_BUILD)</c>). A period of 0 keeps the computed one.
    /// </summary>
    public static IReadOnlyDictionary<uint, uint> Select(IEnumerable<TransportPeriodRow> rows, ushort clientBuild = ClientBuild.Vanilla1121)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(r => r.Build <= clientBuild)
            .GroupBy(r => r.Entry)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Build)!.Period);
    }
}

using System.Data;
using System.Data.Common;

namespace ArcaneCore.Data;

/// <summary>
/// Opens a <see cref="DbConnection"/> that is not open. A connection in <see cref="ConnectionState.Broken"/> (it was open, then lost
/// its link) cannot be opened again until it is closed; calling <c>OpenAsync</c> on it throws <see cref="InvalidOperationException"/>.
/// </summary>
internal static class DbConnectionOpen
{
    /// <summary>
    /// Makes <paramref name="connection"/> open. An open connection is left alone; a broken one is closed first, then opened; any other
    /// state is opened. Returns true when this call opened it (the caller then owns closing it), false when it was already open.
    /// </summary>
    public static async Task<bool> EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State == ConnectionState.Open)
        {
            return false;
        }

        if (connection.State == ConnectionState.Broken)
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

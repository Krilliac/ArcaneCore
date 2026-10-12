using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data;

/// <summary>
/// A read-only transaction that sees one consistent snapshot without taking the write lock. Microsoft.Data.Sqlite opens
/// a Serializable transaction as BEGIN IMMEDIATE (the RESERVED write lock), so a pure read queues behind every commit of
/// the character save queue; on SQLite a deferred transaction is opened instead, whose first read pins one WAL snapshot
/// (the reasoning of <see cref="Economy.EfEconomyStore"/>'s auction snapshot). Other providers keep Serializable.
/// </summary>
internal sealed class ReadSnapshot : IAsyncDisposable
{
    private readonly DbContext _db;
    private readonly bool _closeConnection;

    private ReadSnapshot(DbContext db, IDbContextTransaction transaction, bool closeConnection)
    {
        _db = db;
        Transaction = transaction;
        _closeConnection = closeConnection;
    }

    public IDbContextTransaction Transaction { get; }

    public static async Task<ReadSnapshot> BeginAsync(DbContext db, CancellationToken cancellationToken)
    {
        using IDisposable exempt = Stores.CharacterWriteFence.Exempt(); // read-only: verifying would take a write lock on SQLite
        if (!db.Database.IsSqlite())
            return new ReadSnapshot(db, await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false), closeConnection: false);

        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            IDbContextTransaction transaction = await db.Database.UseTransactionAsync(connection.BeginTransaction(deferred: true),
                    cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("the SQLite snapshot transaction was not attached");
            return new ReadSnapshot(db, transaction, closeConnection: true);
        }
        catch
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken) => Transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait(false);
        // Opened by BeginAsync: EF does not close a connection it was asked to keep open.
        if (_closeConnection) await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
    }
}

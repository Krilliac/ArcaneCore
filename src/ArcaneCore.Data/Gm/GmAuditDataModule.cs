using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Gm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Gm;

/// <summary>One <c>account_mute</c> row: the chat mute of an account (unix seconds).</summary>
public sealed class AccountMuteRow
{
    public int AccountId { get; set; }
    public long MutedUntil { get; set; }
    public long MutedAt { get; set; }
    public string MutedBy { get; set; } = string.Empty;
    public byte MutedBySecurity { get; set; }
    public string Reason { get; set; } = string.Empty;

    public AccountMuteRecord ToRecord() => new(AccountId, MutedUntil, MutedAt, MutedBy, MutedBySecurity, Reason);
}

/// <summary>One <c>gm_ticket</c> row (vmangos <c>character_ticket</c>, widened with a status and the closing staff member).</summary>
public sealed class GmTicketRow
{
    public int Id { get; set; }
    public int CharacterId { get; set; }
    public string Text { get; set; } = string.Empty;
    public byte Category { get; set; }
    public uint MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public byte Status { get; set; }
    public string Response { get; set; } = string.Empty;
    public string ClosedBy { get; set; } = string.Empty;
    public long ClosedAt { get; set; }

    public GmTicketRecord ToRecord()
        => new(Id, CharacterId, Text, Category, MapId, X, Y, Z, CreatedAt, UpdatedAt, (GmTicketStatus)Status, Response, ClosedBy, ClosedAt);

    public void CopyFrom(GmTicketRecord ticket)
    {
        CharacterId = ticket.CharacterId;
        Text = ticket.Text;
        Category = ticket.Category;
        MapId = ticket.MapId;
        X = ticket.X;
        Y = ticket.Y;
        Z = ticket.Z;
        CreatedAt = ticket.CreatedAt;
        UpdatedAt = ticket.UpdatedAt;
        Status = (byte)ticket.Status;
        Response = ticket.Response;
        ClosedBy = ticket.ClosedBy;
        ClosedAt = ticket.ClosedAt;
    }
}

/// <summary>
/// Characters schema module of the GM audit lane (docs/integration/gm-audit-lane.md): <c>account_mute</c> and
/// <c>gm_ticket</c>. The version is one constant the integrator renumbers (next free characters version at this base,
/// after the game-event status step at 25).
/// </summary>
public sealed class GmAuditDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 26;

    public const string MuteTable = "account_mute";

    public const string TicketTable = "gm_ticket";

    public const int MaxTextLength = GmAuditLimits.MaxTextLength;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(MuteTable), new CreateTableChange(TicketTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AccountMuteRow>(entity =>
        {
            entity.ToTable(MuteTable);
            entity.HasKey(r => r.AccountId);
            entity.Property(r => r.AccountId).ValueGeneratedNever();
            entity.Property(r => r.MutedBy).HasMaxLength(50).IsRequired();
            entity.Property(r => r.Reason).HasMaxLength(255).IsRequired();
        });

        modelBuilder.Entity<GmTicketRow>(entity =>
        {
            entity.ToTable(TicketTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedNever();
            entity.Property(r => r.Text).HasMaxLength(MaxTextLength).IsRequired();
            entity.Property(r => r.Response).HasMaxLength(MaxTextLength).IsRequired();
            entity.Property(r => r.ClosedBy).HasMaxLength(50).IsRequired();
            entity.HasIndex(r => r.CharacterId);
            entity.HasIndex(r => r.Status);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGmAuditStore, EfGmAuditStore>();

    /// <summary>The character's tickets (open and closed) go with the character; a mute belongs to the account and stays.</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<GmTicketRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IGmAuditStore"/>.</summary>
public sealed class EfGmAuditStore(CharacterDbContext db) : IGmAuditStore
{
    public async Task<IReadOnlyList<AccountMuteRecord>> LoadActiveMutesAsync(long nowUnix, CancellationToken cancellationToken = default)
        => [.. (await db.Set<AccountMuteRow>().AsNoTracking().Where(r => r.MutedUntil > nowUnix)
            .OrderBy(r => r.AccountId).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToRecord())];

    public async Task SaveMuteAsync(AccountMuteRecord mute, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mute);
        DbSet<AccountMuteRow> set = db.Set<AccountMuteRow>();
        AccountMuteRow? row = await set.FirstOrDefaultAsync(r => r.AccountId == mute.AccountId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new AccountMuteRow { AccountId = mute.AccountId };
            set.Add(row);
        }

        row.MutedUntil = mute.MutedUntil;
        row.MutedAt = mute.MutedAt;
        row.MutedBy = mute.MutedBy;
        row.MutedBySecurity = mute.MutedBySecurity;
        row.Reason = mute.Reason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteMuteAsync(int accountId, CancellationToken cancellationToken = default)
        => await db.Set<AccountMuteRow>().Where(r => r.AccountId == accountId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

    public Task<int> DeleteExpiredMutesAsync(long nowUnix, CancellationToken cancellationToken = default)
        => db.Set<AccountMuteRow>().Where(r => r.MutedUntil <= nowUnix).ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyList<GmTicketRecord>> LoadOpenTicketsAsync(CancellationToken cancellationToken = default)
        => [.. (await db.Set<GmTicketRow>().AsNoTracking().Where(r => r.Status == (byte)GmTicketStatus.Open)
            .OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToRecord())];

    public async Task<int> GetMaxTicketIdAsync(CancellationToken cancellationToken = default)
        => await db.Set<GmTicketRow>().AsNoTracking().Select(r => (int?)r.Id).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;

    public async Task SaveTicketAsync(GmTicketRecord ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (!await db.Characters.AnyAsync(c => c.Id == ticket.CharacterId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        DbSet<GmTicketRow> set = db.Set<GmTicketRow>();
        GmTicketRow? row = await set.FirstOrDefaultAsync(r => r.Id == ticket.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new GmTicketRow { Id = ticket.Id };
            set.Add(row);
        }

        row.CopyFrom(ticket);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteTicketAsync(int ticketId, CancellationToken cancellationToken = default)
        => await db.Set<GmTicketRow>().Where(r => r.Id == ticketId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}

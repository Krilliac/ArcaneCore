using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Names;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Buffers;
using System.Collections.Frozen;

namespace ArcaneCore.Data.Content.Names;

public sealed class ReservedNameRow
{
    public string Name { get; set; } = string.Empty;
}

public sealed class ReservedNameWorldDataModule : IDataModule
{
    public const int Version = 32; // allocated as 21 on the Codex line; renumbered at the 2026-10-07 integration
    public DatabaseComponent Component => DatabaseComponent.World;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("reserved_name")];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ReservedNameRow>(entity =>
        {
            entity.ToTable("reserved_name");
            entity.HasKey(r => r.Name);
            entity.Property(r => r.Name).HasColumnName("name").HasMaxLength(12).IsRequired();
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IReservedNameStore, EfReservedNameStore>();
}

public sealed class EfReservedNameStore(IDbContextFactory<WorldDbContext> factory) : IReservedNameStore
{
    public async Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<string> rows = await db.Set<ReservedNameRow>().AsNoTracking().Select(r => r.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ReservedNameNormalization.Normalize).Where(n => n is not null).Cast<string>().ToFrozenSet(StringComparer.Ordinal);
    }
}

public static class ReservedNameNormalization
{
    public static string? Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        int count = 0;
        ReadOnlySpan<char> span = name;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out _, out int consumed) != OperationStatus.Done) return null;
            span = span[consumed..];
            count++;
        }
        if (count > 12) return null;
        return name.ToLowerInvariant();
    }
}

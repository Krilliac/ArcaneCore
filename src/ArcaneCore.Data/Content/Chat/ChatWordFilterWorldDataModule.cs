using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Content.Chat;

/// <summary>A <c>chat_word_filter</c> row (AscEmu Management/WordFilter.cpp; ArcaneCore table, not part of ClassicDB).</summary>
public sealed class ChatWordFilterRow
{
    public uint Id { get; set; }

    /// <summary>A .NET regular expression, matched case-insensitively.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary><see cref="ChatWordFilterScope"/> flags: 1 chat, 2 names, 3 both.</summary>
    public byte Scope { get; set; } = 1;

    /// <summary><see cref="ChatWordFilterAction"/>: 0 censor, 1 block the message.</summary>
    public byte Action { get; set; }

    public string Comment { get; set; } = string.Empty;
}

/// <summary>World schema 47 (wave 18): the database-driven chat and name filter. Empty by default.</summary>
public sealed class ChatWordFilterWorldDataModule : IDataModule
{
    public const int Version = 47;

    public DatabaseComponent Component => DatabaseComponent.World;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("chat_word_filter")];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ChatWordFilterRow>(entity =>
        {
            entity.ToTable("chat_word_filter");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Pattern).HasColumnName("pattern").HasMaxLength(255).IsRequired();
            entity.Property(r => r.Scope).HasColumnName("scope");
            entity.Property(r => r.Action).HasColumnName("action");
            entity.Property(r => r.Comment).HasColumnName("comment").HasMaxLength(255).IsRequired().HasDefaultValue(string.Empty);
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IChatWordFilterStore, EfChatWordFilterStore>();
}

public sealed class EfChatWordFilterStore(IDbContextFactory<WorldDbContext> factory) : IChatWordFilterStore
{
    public async Task<IReadOnlyList<ChatWordFilterRule>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using WorldDbContext db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<ChatWordFilterRow> rows = await db.Set<ChatWordFilterRow>().AsNoTracking().OrderBy(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Where(r => r.Pattern.Length > 0)
            .Select(r => new ChatWordFilterRule(r.Id, r.Pattern, (ChatWordFilterScope)r.Scope, (ChatWordFilterAction)r.Action))];
    }
}

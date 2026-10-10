using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.ServerMail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.ServerMail;

public sealed class ServerMailTemplateEntity
{
    public uint Id { get; set; }
    public uint SenderEntry { get; set; }
    public uint MoneyA { get; set; }
    public uint MoneyH { get; set; }
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Active { get; set; } = true;
}

public sealed class ServerMailItemEntity
{
    public int Id { get; set; }
    public uint TemplateId { get; set; }
    public string Faction { get; set; } = "Alliance";
    public uint Item { get; set; }
    public uint ItemCount { get; set; }
}

public sealed class ServerMailConditionEntity
{
    public int Id { get; set; }
    public uint TemplateId { get; set; }
    public string ConditionType { get; set; } = "";
    public uint ConditionValue { get; set; }
    public uint ConditionState { get; set; }
}

public sealed class ServerMailCharacterEntity
{
    public int Guid { get; set; }
    public uint MailId { get; set; }
}

/// <summary>
/// Characters schema 54: AzerothCore's server mail tables (mail_server_template, _items, _conditions, mail_server_character): the
/// templates, their items and conditions, and which character already got which letter. Deleting a character drops its sent records.
/// </summary>
public sealed class ServerMailDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 54;
    public const string Templates = "mail_server_template", Items = "mail_server_template_items",
        Conditions = "mail_server_template_conditions", Sent = "mail_server_character";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
        [new CreateTableChange(Templates), new CreateTableChange(Items), new CreateTableChange(Conditions), new CreateTableChange(Sent)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ServerMailTemplateEntity>(e =>
        {
            e.ToTable(Templates);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(r => r.SenderEntry).HasColumnName("senderEntry");
            e.Property(r => r.MoneyA).HasColumnName("moneyA");
            e.Property(r => r.MoneyH).HasColumnName("moneyH");
            e.Property(r => r.Subject).HasColumnName("subject").HasMaxLength(255);
            e.Property(r => r.Body).HasColumnName("body").HasMaxLength(4000);
            e.Property(r => r.Active).HasColumnName("active");
        });
        modelBuilder.Entity<ServerMailItemEntity>(e =>
        {
            e.ToTable(Items);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(r => r.TemplateId).HasColumnName("templateID");
            e.Property(r => r.Faction).HasColumnName("faction").HasMaxLength(16);
            e.Property(r => r.Item).HasColumnName("item");
            e.Property(r => r.ItemCount).HasColumnName("itemCount");
            e.HasIndex(r => r.TemplateId);
        });
        modelBuilder.Entity<ServerMailConditionEntity>(e =>
        {
            e.ToTable(Conditions);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(r => r.TemplateId).HasColumnName("templateID");
            e.Property(r => r.ConditionType).HasColumnName("conditionType").HasMaxLength(32);
            e.Property(r => r.ConditionValue).HasColumnName("conditionValue");
            e.Property(r => r.ConditionState).HasColumnName("conditionState");
            e.HasIndex(r => r.TemplateId);
        });
        modelBuilder.Entity<ServerMailCharacterEntity>(e =>
        {
            e.ToTable(Sent);
            e.HasKey(r => new { r.Guid, r.MailId });
            e.Property(r => r.Guid).HasColumnName("guid");
            e.Property(r => r.MailId).HasColumnName("mailId");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IServerMailStore, EfServerMailStore>();

    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Set<ServerMailCharacterEntity>().RemoveRange(
            await db.Set<ServerMailCharacterEntity>().Where(r => r.Guid == characterId).ToListAsync(cancellationToken).ConfigureAwait(false));
    }
}

public sealed class EfServerMailStore(CharacterDbContext db) : IServerMailStore
{
    public async Task<ServerMailContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        var templates = await db.Set<ServerMailTemplateEntity>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = await db.Set<ServerMailItemEntity>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var conditions = await db.Set<ServerMailConditionEntity>().AsNoTracking().OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ServerMailContent(
            templates.Select(r => new ServerMailTemplateRow(r.Id, r.SenderEntry, r.MoneyA, r.MoneyH, r.Subject, r.Body, r.Active)).ToList(),
            items.Select(r => new ServerMailItemRow(r.TemplateId, r.Faction, r.Item, r.ItemCount)).ToList(),
            conditions.Select(r => new ServerMailConditionRow(r.TemplateId, r.ConditionType, r.ConditionValue, r.ConditionState)).ToList());
    }

    public async Task<IReadOnlyCollection<uint>> GetSentAsync(int characterId, CancellationToken cancellationToken = default)
        => await db.Set<ServerMailCharacterEntity>().AsNoTracking().Where(r => r.Guid == characterId).Select(r => r.MailId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task MarkSentAsync(int characterId, uint templateId, CancellationToken cancellationToken = default)
    {
        if (await db.Set<ServerMailCharacterEntity>().AnyAsync(r => r.Guid == characterId && r.MailId == templateId, cancellationToken).ConfigureAwait(false)) return;
        db.Add(new ServerMailCharacterEntity { Guid = characterId, MailId = templateId });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

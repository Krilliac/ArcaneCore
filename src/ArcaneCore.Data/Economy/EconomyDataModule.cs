using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Economy;

/// <summary>A letter (vmangos <c>mail</c>; the single 1.12 attachment is <c>item_guid</c>).</summary>
public sealed class MailRow
{
    public uint Id { get; set; }
    public byte MessageType { get; set; }
    public uint Stationery { get; set; }
    public uint SenderId { get; set; }
    public int ReceiverId { get; set; }
    public string Subject { get; set; } = string.Empty;
    public uint ItemTextId { get; set; }
    public uint ItemGuid { get; set; }
    public uint ItemEntry { get; set; }
    public uint Money { get; set; }
    public uint Cod { get; set; }
    public uint Checked { get; set; }
    public long DeliverTime { get; set; }
    public long ExpireTime { get; set; }

    public MailRecord ToRecord() => new()
    {
        Id = Id, MessageType = (MailMessageType)MessageType, Stationery = Stationery, SenderId = SenderId,
        ReceiverId = ReceiverId, Subject = Subject, ItemTextId = ItemTextId, ItemGuid = ItemGuid, ItemEntry = ItemEntry,
        Money = Money, Cod = Cod, Checked = (MailCheckMask)Checked, DeliverTime = DeliverTime, ExpireTime = ExpireTime,
    };

    public void CopyFrom(MailRecord mail)
    {
        Id = mail.Id;
        MessageType = (byte)mail.MessageType;
        Stationery = mail.Stationery;
        SenderId = mail.SenderId;
        ReceiverId = mail.ReceiverId;
        Subject = mail.Subject;
        ItemTextId = mail.ItemTextId;
        ItemGuid = mail.ItemGuid;
        ItemEntry = mail.ItemEntry;
        Money = mail.Money;
        Cod = mail.Cod;
        Checked = (uint)mail.Checked;
        DeliverTime = mail.DeliverTime;
        ExpireTime = mail.ExpireTime;
    }
}

/// <summary>Letter text (vmangos <c>item_text</c>), also read by CMSG_ITEM_TEXT_QUERY.</summary>
public sealed class ItemTextRow
{
    public uint Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>An auction (vmangos <c>auction</c>).</summary>
public sealed class AuctionRow
{
    public uint Id { get; set; }
    public uint HouseId { get; set; }
    public uint ItemGuid { get; set; }
    public uint ItemEntry { get; set; }
    public uint ItemCount { get; set; }
    public int SellerId { get; set; }
    public uint StartBid { get; set; }
    public uint Buyout { get; set; }
    public long ExpireTime { get; set; }
    public int BidderId { get; set; }
    public uint Bid { get; set; }
    public uint Deposit { get; set; }

    public AuctionRecord ToRecord() => new()
    {
        Id = Id, HouseId = HouseId, ItemGuid = ItemGuid, ItemEntry = ItemEntry, ItemCount = ItemCount, SellerId = SellerId,
        StartBid = StartBid, Buyout = Buyout, ExpireTime = ExpireTime, BidderId = BidderId, Bid = Bid, Deposit = Deposit,
    };

    public void CopyFrom(AuctionRecord auction)
    {
        Id = auction.Id;
        HouseId = auction.HouseId;
        ItemGuid = auction.ItemGuid;
        ItemEntry = auction.ItemEntry;
        ItemCount = auction.ItemCount;
        SellerId = auction.SellerId;
        StartBid = auction.StartBid;
        Buyout = auction.Buyout;
        ExpireTime = auction.ExpireTime;
        BidderId = auction.BidderId;
        Bid = auction.Bid;
        Deposit = auction.Deposit;
    }
}

/// <summary>The idempotency ledger: one row per committed economy operation.</summary>
public sealed class EconomyOperationRow
{
    public string Id { get; set; } = string.Empty;
    public long CommittedAt { get; set; }
}

/// <summary>
/// Economy tables of the characters database: mail, item text, auctions and the operation
/// ledger (docs/integration/economy.md). Fleet round 2 reserves characters v10 for this module;
/// <see cref="Version"/> holds the next contiguous number at this branch's base until the lead
/// renumbers it at merge time (docs/integration/seams.md, "Schema versions").
/// </summary>
public sealed class EconomyDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single version constant the lead renumbers (reserved final number: 10).</summary>
    public const int Version = 10;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("mail"),
        new CreateTableChange("item_text"),
        new CreateTableChange("auction"),
        new CreateTableChange("economy_operation"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MailRow>(entity =>
        {
            entity.ToTable("mail");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.MessageType).HasColumnName("message_type");
            entity.Property(r => r.Stationery).HasColumnName("stationery");
            entity.Property(r => r.SenderId).HasColumnName("sender_guid");
            entity.Property(r => r.ReceiverId).HasColumnName("receiver_guid");
            entity.Property(r => r.Subject).HasColumnName("subject").HasMaxLength(128).IsRequired();
            entity.Property(r => r.ItemTextId).HasColumnName("item_text_id");
            entity.Property(r => r.ItemGuid).HasColumnName("item_guid");
            entity.Property(r => r.ItemEntry).HasColumnName("item_id");
            entity.Property(r => r.Money).HasColumnName("money");
            entity.Property(r => r.Cod).HasColumnName("cod");
            entity.Property(r => r.Checked).HasColumnName("checked");
            entity.Property(r => r.DeliverTime).HasColumnName("deliver_time");
            entity.Property(r => r.ExpireTime).HasColumnName("expire_time");
            entity.HasIndex(r => r.ReceiverId);
            entity.HasIndex(r => r.ExpireTime);
            entity.HasIndex(r => r.ItemGuid);
        });

        modelBuilder.Entity<ItemTextRow>(entity =>
        {
            entity.ToTable("item_text");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.Text).HasColumnName("text").HasMaxLength(4000).IsRequired();
        });

        modelBuilder.Entity<AuctionRow>(entity =>
        {
            entity.ToTable("auction");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(r => r.HouseId).HasColumnName("house_id");
            entity.Property(r => r.ItemGuid).HasColumnName("item_guid");
            entity.Property(r => r.ItemEntry).HasColumnName("item_id");
            entity.Property(r => r.ItemCount).HasColumnName("item_count");
            entity.Property(r => r.SellerId).HasColumnName("seller_guid");
            entity.Property(r => r.StartBid).HasColumnName("start_bid");
            entity.Property(r => r.Buyout).HasColumnName("buyout_price");
            entity.Property(r => r.ExpireTime).HasColumnName("expire_time");
            entity.Property(r => r.BidderId).HasColumnName("buyer_guid");
            entity.Property(r => r.Bid).HasColumnName("last_bid");
            entity.Property(r => r.Deposit).HasColumnName("deposit");
            entity.HasIndex(r => r.ItemGuid).IsUnique();
            entity.HasIndex(r => r.SellerId);
            entity.HasIndex(r => r.ExpireTime);
        });

        modelBuilder.Entity<EconomyOperationRow>(entity =>
        {
            entity.ToTable("economy_operation");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").HasMaxLength(36).ValueGeneratedNever();
            entity.Property(r => r.CommittedAt).HasColumnName("committed_at");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IEconomyStore, EfEconomyStore>();

    /// <summary>Return, delete or neutralize the character's letters and auctions (<see cref="EconomyCharacterCleanup"/>).</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => EconomyCharacterCleanup.StageAsync(db, characterId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), cancellationToken);
}

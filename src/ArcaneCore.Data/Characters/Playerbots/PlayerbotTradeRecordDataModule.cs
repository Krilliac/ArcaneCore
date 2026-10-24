using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Playerbots;

public sealed class PlayerbotTradeRecordRow
{
    public Guid BotId { get; set; }
    public int CharacterId { get; set; }
    public ulong OtherGuid { get; set; }
    public int FairDeals { get; set; }
    public int Scams { get; set; }
    public long LastScamUnixMs { get; set; }
    public long LastNinjaUnixMs { get; set; }
    public int LastGiftDay { get; set; }
}

/// <summary>Characters schema 65: each managed playerbot's trade ledger (fair deals, scams, ninja loot, last gift) per partner.</summary>
public sealed class PlayerbotTradeRecordDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 65;
    public const string Table = "playerbot_trade_record";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    // TEST-AUTHOR STUB: the model, the table change, the store and the cleanup are the implementer's (P4 S2).
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IPlayerbotTradeRecordStore, EfPlayerbotTradeRecordStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class EfPlayerbotTradeRecordStore(CharacterDbContext db) : IPlayerbotTradeRecordStore
{
    internal CharacterDbContext Db => db;

    public Task<IReadOnlyList<PlayerbotTradeRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException("P4 S2: load playerbot_trade_record");

    public Task ReplaceAsync(Guid botId, IReadOnlyList<PlayerbotTradeRecord> rows, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("P4 S2: replace playerbot_trade_record rows");
}

namespace ArcaneCore.Kernel.Accounts;

public sealed record ManagedPlayerbotProvision(Guid BotId, int AccountId, string AccountName, long CreatedUnix);

/// <summary>Durable proof for newly created private bot accounts, pending cross-database character provisioning.</summary>
public interface IManagedPlayerbotProvisionStore
{
    // Creates the new P0 account and its pending proof in one auth transaction. Never adopts an existing account.
    Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default);
    // Caller must have verified that no character remains. Refuses changed identity, security or a issued session key.
    Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default);
}

namespace ArcaneCore.Kernel.Realms;

/// <summary>
/// How many characters an account has on each realm, kept in the auth database so the logon daemon can fill the realm list's
/// per-realm character count without reaching a characters database (vmangos <c>realmcharacters</c>: written by mangosd,
/// Handlers/CharacterHandler.cpp:314 and World.cpp:2891-2905; read by realmd, AuthSocket.cpp:1062).
/// </summary>
public interface IRealmCharacterCountStore
{
    /// <summary>The counts of one account by realm id; a realm without a row is absent (read it as 0).</summary>
    Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Record that <paramref name="accountId"/> has <paramref name="count"/> characters on <paramref name="realmId"/> (insert or replace; negative counts are stored as 0).</summary>
    Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default);
}

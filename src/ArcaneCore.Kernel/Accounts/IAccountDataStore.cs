namespace ArcaneCore.Kernel.Accounts;

/// <summary>
/// One client-side settings blob (macros, key bindings, layout …) the client stores on the
/// server through CMSG_UPDATE_ACCOUNT_DATA (vmangos AccountData; 8 types in 1.12). Kept as the
/// exact decompressed bytes: SMSG_ACCOUNT_DATA_MD5 must hash what the client sent, so the data
/// is never round-tripped through a text encoding. <see cref="Time"/> is the server's Unix time
/// of the last update (1.12 clients never see it; later clients do).
/// </summary>
public sealed record AccountDataEntry(uint Time, byte[] Data);

/// <summary>An account's stored settings blobs and tutorial flags.</summary>
public sealed class AccountSettings
{
    /// <summary>Account data types for build 5875 (gtker AccountDataType, vmangos NUM_ACCOUNT_DATA_TYPES).</summary>
    public const int DataTypeCount = 8;

    /// <summary>Tutorial flag words (SMSG_TUTORIAL_FLAGS carries 8 × u32).</summary>
    public const int TutorialWordCount = 8;

    public AccountDataEntry?[] Data { get; } = new AccountDataEntry?[DataTypeCount];

    public uint[] Tutorials { get; } = new uint[TutorialWordCount];
}

/// <summary>Persistence seam for per-account client settings (stored per realm, like vmangos).</summary>
public interface IAccountDataStore
{
    Task<AccountSettings> GetAsync(int accountId, CancellationToken cancellationToken = default);

    /// <summary>Store (or, with empty <see cref="AccountDataEntry.Data"/>, erase) one account data blob.</summary>
    Task SaveDataAsync(int accountId, int type, AccountDataEntry entry, CancellationToken cancellationToken = default);

    Task SaveTutorialsAsync(int accountId, IReadOnlyList<uint> tutorials, CancellationToken cancellationToken = default);
}

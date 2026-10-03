namespace ArcaneCore.Kernel.Social;

/// <summary>One signature on a guild charter (vmangos petition_sign: player_guid, player_account).</summary>
public sealed record PetitionSignatureData(int PlayerId, int AccountId);

/// <summary>
/// A guild charter petition with its signatures (vmangos petition + petition_sign): the petition id
/// (also written into the charter item's enchantment slot 0), the owner, the charter item's guid,
/// the proposed guild name and who signed. The team is not stored: it is the owner's
/// (vmangos Petition::LoadFromDB, GuildMgr.cpp:340).
/// </summary>
public sealed record PetitionData(int Id, int OwnerId, int CharterItemId, string Name, IReadOnlyList<PetitionSignatureData> Signatures);

/// <summary>Petitions in the characters database.</summary>
public interface IPetitionStore
{
    /// <summary>Every petition with its signatures (startup).</summary>
    Task<IReadOnlyList<PetitionData>> GetPetitionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or replace a petition with exactly these signatures in one SaveChanges.</summary>
    Task SavePetitionAsync(PetitionData petition, CancellationToken cancellationToken = default);

    /// <summary>Delete a petition and its signatures; a missing petition is a no-op.</summary>
    Task DeletePetitionAsync(int petitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turn-in: store <paramref name="guild"/> (guild, ranks, members) and delete the petition and its
    /// signatures in ONE transaction. Fails as a whole (nothing stored, the petition kept) when a
    /// member already belongs to another guild (guild_member has a unique character index).
    /// </summary>
    Task CompletePetitionAsync(GuildData guild, int petitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A deleted character's leftovers: the petition it owns with its signatures, and its signatures
    /// on other petitions. Applies only while the id has no character row, so a character recreated
    /// with the same id keeps its own (docs/integration/character-delete.md).
    /// </summary>
    Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

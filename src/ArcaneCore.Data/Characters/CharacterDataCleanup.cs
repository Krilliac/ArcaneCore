using ArcaneCore.Data.Schema;

namespace ArcaneCore.Data.Characters;

/// <summary>
/// The deletion half of a characters-database <see cref="IDataModule"/>: remove (or neutralize)
/// every row the module keeps for a character that is being deleted, including rows of other
/// characters that point at it (vmangos <c>Player::DeleteFromDB</c>, which clears each per-character
/// table and <c>character_social WHERE friend = guid</c> in one transaction).
/// <para>
/// Every characters module must implement this; <c>CharacterDeletionTests</c> fails a module that
/// adds per-character tables without saying how they are deleted. The module is discovered with
/// the other <see cref="IDataModule"/>s, so later features (reputation, mail, instance binds …)
/// plug in without editing <see cref="Stores.EfCharacterStore"/> (docs/integration/character-delete.md).
/// </para>
/// </summary>
public interface ICharacterDataCleanup
{
    /// <summary>
    /// Runs inside the deletion transaction on <paramref name="db"/>, before the <c>characters</c>
    /// row is removed. Stage tracked removals or run set-based deletes (they join the transaction);
    /// the caller saves and commits. Throw <see cref="CharacterDeletionRefusedException"/> to refuse
    /// the deletion (everything rolls back); any other exception also rolls back and fails it.
    /// </summary>
    Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken);
}

/// <summary>A module refused to delete a character (for example a guild leader, vmangos HandleCharDeleteOpcode).</summary>
public sealed class CharacterDeletionRefusedException(string message) : InvalidOperationException(message);

/// <summary>Discovery of the <see cref="ICharacterDataCleanup"/> modules.</summary>
public static class CharacterDataCleanups
{
    /// <summary>Every characters module's cleanup, in module (full type name) order.</summary>
    public static IReadOnlyList<ICharacterDataCleanup> All { get; } =
        [.. DataModules.For(DatabaseComponent.Characters).OfType<ICharacterDataCleanup>()];

    /// <summary>Characters modules that do not implement <see cref="ICharacterDataCleanup"/> (should be none).</summary>
    public static IReadOnlyList<IDataModule> Missing { get; } =
        [.. DataModules.For(DatabaseComponent.Characters).Where(m => m is not ICharacterDataCleanup)];
}

using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Characters;

/// <summary>
/// Character deletion hooks for features that keep live per-character state (caches, write
/// queues, friend lists, groups, guilds …). Implement on an <see cref="Features.IWorldFeature"/>,
/// which is then also registered as an <see cref="ICharacterDeleteHook"/>. Every method has a
/// default that does nothing. All run on the session task; reach world state through
/// <see cref="Game.Maps.WorldRuntime.InvokeAsync{T}"/>.
/// <para>
/// The stored rows are removed by the characters-database modules
/// (<see cref="Data.Characters.ICharacterDataCleanup"/>) in one transaction. These hooks cover the
/// rest, in vmangos HandleCharDeleteOpcode → Player::DeleteFromDB order: refuse
/// (<see cref="CanDeleteCharacterAsync"/>), drain this character's queued writes so none lands
/// after the rows are gone (<see cref="OnCharacterDeletingAsync"/>), then drop the live state once
/// the rows are committed (<see cref="OnCharacterDeletedAsync"/>). See
/// docs/integration/character-delete.md.
/// </para>
/// </summary>
public interface ICharacterDeleteHook
{
    /// <summary>Before anything changes. Return false to refuse (CHAR_DELETE_FAILED); an exception refuses too.</summary>
    Task<bool> CanDeleteCharacterAsync(WorldSession session, CharacterRecord character) => Task.FromResult(true);

    /// <summary>
    /// Every hook allowed the deletion and the rows are about to be removed: wait for this
    /// character's queued writes. An exception refuses the deletion; nothing is removed.
    /// </summary>
    Task OnCharacterDeletingAsync(WorldSession session, CharacterRecord character) => Task.CompletedTask;

    /// <summary>
    /// The rows are gone: drop caches, list entries and memberships that point at it. The
    /// character directory still knows the character (for names in notifications); it forgets it
    /// after the last hook, and a hook may remove it earlier. An exception is logged; the others still run.
    /// </summary>
    Task OnCharacterDeletedAsync(WorldSession session, CharacterRecord character) => Task.CompletedTask;
}

/// <summary>CMSG_CHAR_DELETE (vmangos WorldSession::HandleCharDeleteOpcode) with the <see cref="ICharacterDeleteHook"/>s.</summary>
public static class CharacterDeletion
{
    /// <summary>
    /// Delete <paramref name="rawGuid"/> for the session's account. False (CHAR_DELETE_FAILED) for a
    /// character that is unknown, owned by another account, still in the world, refused by a hook
    /// or whose stored rows could not be removed; in those cases nothing was removed.
    /// </summary>
    public static async Task<bool> TryDeleteAsync(WorldSession session, ulong rawGuid)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (rawGuid == 0 || rawGuid > int.MaxValue)
        {
            return false;
        }

        int id = (int)rawGuid;
        var guid = ObjectGuid.Player((uint)id);
        ICharacterStore characters = session.Services.GetRequiredService<ICharacterStore>();
        ICharacterDeleteHook[] hooks = [.. session.Services.GetServices<ICharacterDeleteHook>()];
        CharacterRecord? character;
        try
        {
            // A character still in the world (e.g. a lingering previous session) is not deletable.
            if (session.World.IsOnline(guid))
            {
                return false;
            }

            character = await characters.GetByIdAsync(id).ConfigureAwait(false);
            if (character is null || character.AccountId != session.AccountId)
            {
                return false;
            }

            foreach (ICharacterDeleteHook hook in hooks)
            {
                if (!await hook.CanDeleteCharacterAsync(session, character).ConfigureAwait(false))
                {
                    session.Logger.LogInformation("[{Endpoint}] {Hook} refused to delete character {Id}",
                        session.RemoteEndpoint, hook.GetType().Name, id);
                    return false;
                }
            }

            // Queued snapshots of this character must not land after its rows are removed.
            if (session.Services.GetService<CharacterSaveQueue>() is { } saves)
            {
                await saves.FlushCharacterAsync(id).ConfigureAwait(false);
            }

            foreach (ICharacterDeleteHook hook in hooks)
            {
                await hook.OnCharacterDeletingAsync(session, character).ConfigureAwait(false);
            }

            if (session.World.IsOnline(guid)
                || !await characters.DeleteAsync(id, session.AccountId).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogError(ex, "[{Endpoint}] could not delete character {Id}", session.RemoteEndpoint, id);
            return false;
        }

        session.Services.GetService<CharacterSaveQueue>()?.ForgetCharacter(id);
        foreach (ICharacterDeleteHook hook in hooks)
        {
            try
            {
                await hook.OnCharacterDeletedAsync(session, character).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                session.Logger.LogError(ex, "[{Endpoint}] {Hook} failed to clean up deleted character {Id}",
                    session.RemoteEndpoint, hook.GetType().Name, id);
            }
        }

        session.Services.GetRequiredService<CharacterDirectory>().Remove(id);
        session.Logger.LogInformation("[{Endpoint}] '{Account}' deleted character '{Name}'",
            session.RemoteEndpoint, session.AccountName, character.Name);
        return true;
    }
}

using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.RealmList;

/// <summary>
/// Keeps the logon daemon's per-realm character count of an account (<see cref="IRealmCharacterCountStore"/>) in step with the characters
/// database. vmangos does it in two places: it adds one when a character is created (CharacterHandler.cpp:312-314) and recounts the account
/// when one is deleted (Player.cpp:4492 → World.cpp:2891-2905, <c>REPLACE INTO realmcharacters</c>); here every refresh is a recount, which also
/// repairs a count that drifted (a failed write, characters that existed before the table did).
/// <para>
/// Runs on the session task (never the world thread), after the client has its answer, and is awaited so one account's refreshes cannot
/// finish out of order. A failed write is logged and never fails the create, delete or character list. Does nothing when
/// <see cref="RealmStatusOptions.Id"/> is 0 or no auth database is registered.
/// </para>
/// </summary>
public static class RealmCharacterCounts
{
    /// <summary>Recount the session's account on this realm and store it; <paramref name="known"/> is the count when the caller just listed the characters.</summary>
    public static async Task RefreshAsync(WorldSession session, int? known = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        IServiceProvider services = session.Services;
        int realmId = services.GetService<IOptions<RealmStatusOptions>>()?.Value.Id ?? 0;
        if (realmId <= 0 || services.GetService<IServiceScopeFactory>() is not { } scopes)
        {
            return;
        }

        try
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (scope.ServiceProvider.GetService<IRealmCharacterCountStore>() is not { } store)
            {
                return; // no auth database in this host
            }

            int count = known ?? await services.GetRequiredService<ICharacterStore>().CountByAccountAsync(session.AccountId).ConfigureAwait(false);
            await store.SetCountAsync(realmId, session.AccountId, count).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the connection or the world is going away
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Logger.LogFault("realm.character-count", ex, "[{Endpoint}] could not store the character count of account {Account} for the realm list", session.RemoteEndpoint, session.AccountId);
        }
    }
}

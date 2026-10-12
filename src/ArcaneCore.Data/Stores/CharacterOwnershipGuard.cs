using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

/// <summary>Shared staged-save fence used by core, economy and quest transactions.</summary>
internal static class CharacterOwnershipGuard
{
    public static async Task VerifyAsync(CharacterDbContext db, CharacterOwnershipToken? token, CancellationToken cancellationToken)
    {
        // The schema row doubles as a serialized inactive gate. A local-mode save locks it for
        // the caller's transaction, so a claim cannot slip in after the missing-token decision.
        CharacterOwnershipRow? row = await db.Set<CharacterOwnershipRow>()
            .AsNoTracking().SingleOrDefaultAsync(r => r.Scope == "world", cancellationToken).ConfigureAwait(false);
        if (row is null)
            throw new InvalidOperationException("the characters ownership fence row is missing; schema initialization is incomplete");

        int changed;
        if (token is null)
        {
            changed = await db.Set<CharacterOwnershipRow>()
                .Where(r => r.Scope == "world" && !r.Active && r.WriteSequence < long.MaxValue)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
                .ConfigureAwait(false);
            if (changed == 0)
                throw new CharacterOwnershipRejectedException("world", row.Epoch);
        }
        else
        {
            if (!string.Equals(token.Scope, "world", StringComparison.Ordinal))
                throw new CharacterOwnershipRejectedException(token.Scope, token.Epoch);
            changed = await db.Set<CharacterOwnershipRow>()
                .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch
                    && r.OwnerIncarnation == token.Incarnation.ToString("D"))
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
                .ConfigureAwait(false);
            if (changed == 0)
                throw new CharacterOwnershipRejectedException(token.Scope, token.Epoch);
        }
    }
}

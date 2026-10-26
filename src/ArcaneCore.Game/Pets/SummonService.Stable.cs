using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Characters.Pets;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Pets;

/// <summary>The cache and write-ordering seams the hunter pet stable (<see cref="PetStableController"/>) needs.</summary>
public sealed partial class SummonService
{
    /// <summary>Replace (null: drop) the cached current pet of a character without writing it; a read already in flight is discarded.</summary>
    public void ReplaceCachedPet(int characterId, PersistentPetSnapshot? snapshot)
    {
        lock (_cacheGate)
        {
            if (snapshot is null) _cached.Remove(characterId);
            else _cached[characterId] = snapshot;
            _cacheGeneration[characterId] = _cacheGeneration.GetValueOrDefault(characterId) + 1;
        }
    }

    /// <summary>
    /// Run <paramref name="write"/> after every earlier pet write of the character and before any later one. When
    /// <paramref name="replacedCurrentRow"/> says the finished write replaced or removed the character's character_pet row (a stable or
    /// unstable), a failed abandon delete still owed for that row is cancelled: the store deletes by character, so running it later would
    /// destroy the pet the stable just wrote.
    /// </summary>
    public async Task<T> QueueSerializedWriteAsync<T>(int characterId, Func<CancellationToken, Task<T>> write, Func<T, bool>? replacedCurrentRow = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        Task prior;
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_cacheGate)
        {
            prior = _saveTails.GetValueOrDefault(characterId) ?? Task.CompletedTask;
            _saveTails[characterId] = done.Task;
        }

        try
        {
            try
            {
                await prior.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("pet.stable", ex);
                AnomalyLedger.Shared.Report("pet", "stable-fault", $"character {characterId}", ex.Message);
                _logger.LogWarning(ex, "previous persistent pet write failed for character {CharacterId}; continuing with the stable write", characterId);
            }

            T result = await write(CancellationToken.None).ConfigureAwait(false);
            if (replacedCurrentRow?.Invoke(result) == true)
            {
                lock (_cacheGate) _failedDeletes.Remove(characterId);
            }
            done.SetResult();
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException)
            {
                FaultLedger.Shared.Record("pet.stable", ex);
                AnomalyLedger.Shared.Report("pet", "stable-fault", $"character {characterId}", ex.Message);
                _logger.LogFault("pet-stable", ex, "persistent pet stable write failed for character {CharacterId}", characterId);
            }

            done.SetException(ex);
            throw;
        }
    }
}
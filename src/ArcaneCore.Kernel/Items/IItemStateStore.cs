namespace ArcaneCore.Kernel.Items;

/// <summary>Reads the stored per-character item state that is not part of the item rows (characters database).</summary>
public interface IItemStateStore
{
    /// <summary>The selected ammo entry of a character (0 when none is stored).</summary>
    Task<uint> GetAmmoAsync(int characterId, CancellationToken cancellationToken = default);
}

namespace ArcaneCore.Kernel.Npc;

/// <summary>The unpaid tail of a player's flight, retained across logout (vmangos PlayerTaxi::m_TaxiDestinations).</summary>
public sealed record TaxiFlightRoute(uint[] Nodes, uint[] PathIds, uint[] LegCosts)
{
    public bool IsValid => Nodes.Length is >= 2 and <= 256 && PathIds.Length == Nodes.Length - 1
        && LegCosts.Length == PathIds.Length && Nodes.All(n => n is > 0 and <= 256)
        && PathIds.All(id => id != 0);
}

/// <summary>Character-owned taxi route persistence. The player position is saved by the normal character snapshot.</summary>
public interface ICharacterTaxiFlightStore
{
    Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default);

    Task DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}

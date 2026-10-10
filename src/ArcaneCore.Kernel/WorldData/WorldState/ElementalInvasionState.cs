namespace ArcaneCore.Kernel.WorldData.WorldState;

/// <summary>One element's vmangos saved variables VAR_FIRE / VAR_AIR / VAR_EARTH / VAR_WATER (stage) and VAR_*_KILLS.</summary>
public readonly record struct ElementalInvasionState(int Element, int Stage, int Kills);

/// <summary>The saved elemental invasion stages (characters database).</summary>
public interface IElementalInvasionStore
{
    Task<IReadOnlyList<ElementalInvasionState>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ElementalInvasionState state, CancellationToken cancellationToken = default);
}

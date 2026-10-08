using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>ScriptDev2's WorldObject::SpawnCreature(guid) for database rows that are absent from ordinary grid loads
/// (for example Deadmines' patrols with spawnMask 0 in ClassicDB z2815).</summary>
public sealed partial class CreatureMapSystem
{
    private readonly HashSet<uint> _scriptOnlySpawns = [];
    private readonly HashSet<uint> _activatingScriptSpawns = [];

    /// <summary>Keep these database GUIDs off ordinary grid loads until a dungeon script activates them.</summary>
    public void RegisterScriptOnlySpawns(IEnumerable<uint> guids)
    {
        ArgumentNullException.ThrowIfNull(guids);
        foreach (uint guid in guids)
        {
            _scriptOnlySpawns.Add(guid);
            Creature? loaded = _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == guid);
            if (loaded is not null)
            {
                Despawn(loaded);
            }
        }
    }

    /// <summary>Spawn one of the registered rows by its database GUID at that row's coordinates.
    /// The creature retains its spawn row and normal respawn behaviour; missing rows or templates spawn nothing.</summary>
    public Creature? SpawnScripted(uint guid)
    {
        if (!_scriptOnlySpawns.Contains(guid))
        {
            return null;
        }

        CreatureSpawn? spawn = _spawnsByGrid.Values.SelectMany(spawns => spawns).FirstOrDefault(s => s.Guid == guid);
        if (spawn is null)
        {
            return null;
        }

        if (_creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == guid) is { } existing)
        {
            return existing;
        }

        _activatingScriptSpawns.Add(guid);
        try
        {
            GridCoord coord = ComputeGrid(spawn.X, spawn.Y);
            LoadedGrid grid = LoadGrid(coord);
            if (_creatures.Values.All(c => c.Spawn?.Guid != guid))
            {
                LoadSpawns(grid, [spawn]);
            }

            return _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == guid);
        }
        finally
        {
            _activatingScriptSpawns.Remove(guid);
        }
    }
}

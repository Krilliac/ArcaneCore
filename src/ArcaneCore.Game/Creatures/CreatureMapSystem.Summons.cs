using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

// Summoned units (docs/integration/pets.md). Kept in its own file so the pets area never edits the
// creature-ai lane's SpawnTemporary / EventAI summon code; it only adds a spawn path that takes a
// HighGuid and lets the caller fill the owner links before the create block is built.
public sealed partial class CreatureMapSystem
{
    /// <summary>The creature content this system spawns from (templates for spell summons).</summary>
    public CreatureContent Content => _content;

    /// <summary>
    /// Put a summoned creature into the map. Unlike <see cref="SpawnTemporary"/> the GUID may be a
    /// HIGHGUID_PET one (pets, guardians, mini pets; vmangos SpellEffects.cpp) and
    /// <paramref name="prepare"/> runs on the built creature before it is added, so the owner
    /// links, faction, level and position are in its create block (vmangos fills them before
    /// <c>Map::Add</c>). <paramref name="prepare"/> returns the position (it needs the creature's
    /// own bounding radius). <paramref name="guidEntry"/> replaces the template entry in the GUID
    /// (a pet's pet number). The creature does not respawn and is announced like a runtime spawn.
    /// </summary>
    internal Creature SpawnSummoned(CreatureTemplate template, HighGuid highGuid, Func<Creature, CreatureHome> prepare, uint guidEntry = 0)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(prepare);
        var creature = new Creature(_nextTemporaryCounter++ & 0x00FFFFFF, template, spawn: null, _content, _random, highGuid, guidEntry);
        creature.MapId = Map.MapId;
        CreatureHome home = prepare(creature);
        creature.SetHome(home);
        creature.ResetToHome(_serverTime());
        creature.IsNewObject = true;

        GridCoord grid = ComputeGrid(home.X, home.Y);
        if (!_grids.TryGetValue(grid, out LoadedGrid? loaded))
        {
            loaded = LoadGrid(grid);
        }

        AddToWorld(creature, loaded);
        return creature;
    }

    /// <summary>
    /// vmangos WorldObject::UpdateGroundPositionZ: the terrain height plus 0.05 when the map has
    /// height data at (x, y), else null.
    /// </summary>
    internal float? GroundZ(float x, float y, float z)
        => _height.GetHeight(Map.MapId, x, y, z) is { } height ? height + 0.05f : null;
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.World.WorldState;

/// <summary>One original Necrotic Shard per imported summon circle during each zone attack.</summary>
internal sealed class InvasionCircleAi(ScourgeInvasionFeature feature) : IGameObjectAi
{
    private readonly Dictionary<uint, uint> _seededZones = [];

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
        if (go.Spawn is not { } spawn || feature.CircleZone(go) is not { } zone
            || !feature.IsCircleAttackActive(zone))
            return;

        if (objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures) return;
        if (!feature.HasLivingCircleOwner(go, zone, creatures))
        {
            DeactivateCamp(objects, go, creatures, zone);
            return;
        }

        // Imported circles have a negative respawn time: the event loads them dormant for scripts to activate.
        if (!go.IsSpawned && spawn.SpawnTimeSeconds < 0) objects.ForceRespawn(go);
        if (!go.IsSpawned) return;

        foreach (GameObject doodad in objects.GameObjects.Where(d => ScourgeInvasionCatalog.CampDoodads.Contains(d.Entry)
            && d.Spawn is { SpawnTimeSeconds: < 0 }
            && feature.IsZoneEventObject(d, zone) && DistanceSquared(d, go) <= 50f * 50f))
            if (!doodad.IsSpawned) objects.ForceRespawn(doodad);

        if (_seededZones.ContainsKey(spawn.Guid)) return;

        bool existing = creatures.Creatures.Any(c => c.IsAlive
            && (c.Entry is ScourgeInvasionCatalog.NecroticShard or ScourgeInvasionCatalog.DamagedNecroticShard)
            && DistanceSquared(c, go) <= 9f);
        if (existing || creatures.SummonForInstance(ScourgeInvasionCatalog.NecroticShard,
            go.X, go.Y, go.Z, go.Orientation) is not null)
            _seededZones[spawn.Guid] = zone;
    }

    private void DeactivateCamp(GameObjectMapSystem objects, GameObject circle, CreatureMapSystem creatures, uint zone)
    {
        objects.DespawnForRespawn(circle);
        foreach (GameObject doodad in objects.GameObjects.Where(d => ScourgeInvasionCatalog.CampDoodads.Contains(d.Entry)
            && feature.IsZoneEventObject(d, zone) && DistanceSquared(d, circle) <= 50f * 50f))
            objects.DespawnForRespawn(doodad);
        foreach (Creature shard in creatures.Creatures.Where(c => c.IsAlive
            && (c.Entry is ScourgeInvasionCatalog.NecroticShard or ScourgeInvasionCatalog.DamagedNecroticShard)
            && DistanceSquared(c, circle) <= 9f).ToArray())
            creatures.ForcedDespawn(shard, 0);
    }

    public void ForgetZone(uint zoneId)
    {
        foreach (uint guid in _seededZones.Where(p => p.Value == zoneId).Select(p => p.Key).ToArray())
            _seededZones.Remove(guid);
    }

    internal static float DistanceSquared(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}

/// <summary>Shard death upgrades the camp, then sends its final relay communique when the damaged shard dies.</summary>
internal sealed class NecroticShardAi(Creature creature) : CreatureAI(creature)
{
    private uint _checkMs = 25_000;
    private uint _minionMs = 5_000;
    private int _finderCapacity;
    private int _campType = Random.Shared.Next(3);

    public override void OnRespawn()
    {
        _checkMs = 25_000;
        _minionMs = 5_000;
        _finderCapacity = 0;
    }

    private void AdoptCampType(int campType) => _campType = campType;

    public override void OnUpdate(uint diffMs)
    {
        _checkMs = _checkMs > diffMs ? _checkMs - diffMs : 0;
        if (_checkMs == 0)
        {
            _checkMs = 25_000;
            bool hasCircle = Me.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects.Any(go => go.IsSpawned
                && go.Entry == ScourgeInvasionCatalog.SummonCircle
                && InvasionCircleAi.DistanceSquared(Me, go) <= 9f) == true;
            if (!hasCircle)
            {
                System?.ForcedDespawn(Me, 0);
                return;
            }
        }

        _minionMs = _minionMs > diffMs ? _minionMs - diffMs : 0;
        if (_minionMs == 0)
        {
            _minionMs = 5_000;
            SpawnFromFinders();
        }
    }

    private void SpawnFromFinders()
    {
        if (System is not { } system) return;
        Creature[] finders = [.. system.Creatures.Where(c => c.Entry == ScourgeInvasionCatalog.MinionFinder
            && c.IsAlive && InvasionCircleAi.DistanceSquared(c, Me) <= 60f * 60f)
            .OrderBy(c => InvasionCircleAi.DistanceSquared(c, Me))];
        int minions = system.Creatures.Count(c => c.IsAlive && ScourgeInvasionCatalog.CampMinions.Contains(c.Entry)
            && InvasionCircleAi.DistanceSquared(c, Me) <= 60f * 60f);
        if (_finderCapacity == 0) _finderCapacity = finders.Length;
        if (minions >= _finderCapacity || finders.Length == 0) return;

        uint spawner = _campType switch
        {
            0 => ScourgeInvasionCatalog.GhostGhoulSpawner,
            1 => ScourgeInvasionCatalog.GhostSkeletonSpawner,
            _ => ScourgeInvasionCatalog.GhoulSkeletonSpawner,
        };
        int count = Math.Min(Random.Shared.Next(1, 4), _finderCapacity - minions);
        foreach (Creature finder in finders.Where(f => !system.Creatures.Any(c => c.IsAlive
            && ScourgeInvasionCatalog.CampMinions.Contains(c.Entry)
            && InvasionCircleAi.DistanceSquared(c, f) <= 15f * 15f)).Take(count))
        {
            if (system.SummonAt(Me, spawner, finder.X, finder.Y, finder.Z, finder.Orientation,
                target: null, despawnMs: 10_000) is null) continue;
            system.SetNextRespawnDelay(finder, (uint)Random.Shared.Next(150, 201));
            system.ForcedDespawn(finder, 0);
        }
    }

    public override void OnDeath(Unit? killer)
    {
        if (Me.Entry == ScourgeInvasionCatalog.NecroticShard)
        {
            Creature? damaged = System?.SummonForInstance(ScourgeInvasionCatalog.DamagedNecroticShard,
                Me.X, Me.Y, Me.Z, Me.Orientation);
            if (damaged?.AI is NecroticShardAi ai) ai.AdoptCampType(_campType);
            return;
        }

        Creature? relay = System?.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.NecropolisRelay, 200f)
            .Where(c => c.IsAlive).MinBy(c => InvasionCircleAi.DistanceSquared(Me, c));
        if (relay is not null) DoCast(relay, ScourgeInvasionCatalog.CampDeathCommunique, triggered: true);
    }
}

/// <summary>The imported invisible camp spawner chooses one minion, then leaves its finder to respawn.</summary>
internal sealed class ScourgeCampSpawnerAi(Creature creature) : CreatureAI(creature)
{
    private uint _spawnMs = (uint)Random.Shared.Next(2_000, 5_001);

    public override void OnUpdate(uint diffMs)
    {
        _spawnMs = _spawnMs > diffMs ? _spawnMs - diffMs : 0;
        if (_spawnMs != 0 || System is not { } system) return;

        bool rare = Random.Shared.Next(217) == 0;
        uint[] choices = Me.Entry switch
        {
            ScourgeInvasionCatalog.GhostGhoulSpawner => rare ? [16379u, 14697u] : [16298u, 16141u],
            ScourgeInvasionCatalog.GhostSkeletonSpawner => rare ? [16379u, 16380u] : [16298u, 16299u],
            _ => rare ? [14697u, 16380u] : [16141u, 16299u],
        };
        system.SummonAt(Me, choices[Random.Shared.Next(choices.Length)], Me.X, Me.Y, Me.Z,
            Me.Orientation, target: null, despawnMs: 3_600_000);
        system.ForcedDespawn(Me, 0);
    }
}

internal sealed class NecropolisRelayAi(Creature creature) : CreatureAI(creature)
{
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id != ScourgeInvasionCatalog.CampDeathCommunique) return;
        Creature? proxy = System?.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.NecropolisProxy, 200f)
            .Where(c => c.IsAlive).MinBy(c => InvasionCircleAi.DistanceSquared(Me, c));
        if (proxy is not null) DoCast(proxy, ScourgeInvasionCatalog.CampDeathCommunique, triggered: true);
    }
}

internal sealed class NecropolisProxyAi(Creature creature) : CreatureAI(creature)
{
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id != ScourgeInvasionCatalog.CampDeathCommunique) return;
        Creature? health = System?.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.NecropolisHealth, 200f)
            .Where(c => c.IsAlive).MinBy(c => InvasionCircleAi.DistanceSquared(Me, c));
        if (health is not null) DoCast(health, ScourgeInvasionCatalog.CampDeathCommunique, triggered: true);
    }
}

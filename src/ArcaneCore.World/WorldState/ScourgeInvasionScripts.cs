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

        // Imported circles have a negative respawn time: the event loads them dormant for scripts to activate.
        if (!go.IsSpawned && spawn.SpawnTimeSeconds < 0) objects.ForceRespawn(go);
        if (!go.IsSpawned || _seededZones.ContainsKey(spawn.Guid)
            || go.Map?.FindUpdater<CreatureMapSystem>() is not { } creatures)
            return;

        bool existing = creatures.Creatures.Any(c => c.IsAlive
            && (c.Entry is ScourgeInvasionCatalog.NecroticShard or ScourgeInvasionCatalog.DamagedNecroticShard)
            && DistanceSquared(c, go) <= 9f);
        if (existing || creatures.SummonForInstance(ScourgeInvasionCatalog.NecroticShard,
            go.X, go.Y, go.Z, go.Orientation) is not null)
            _seededZones[spawn.Guid] = zone;
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

    public override void OnRespawn() => _checkMs = 25_000;

    public override void OnUpdate(uint diffMs)
    {
        _checkMs = _checkMs > diffMs ? _checkMs - diffMs : 0;
        if (_checkMs != 0) return;
        _checkMs = 25_000;
        bool hasCircle = Me.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects.Any(go => go.IsSpawned
            && go.Entry == ScourgeInvasionCatalog.SummonCircle
            && InvasionCircleAi.DistanceSquared(Me, go) <= 9f) == true;
        if (!hasCircle) System?.ForcedDespawn(Me, 0);
    }

    public override void OnDeath(Unit? killer)
    {
        if (Me.Entry == ScourgeInvasionCatalog.NecroticShard)
        {
            System?.SummonForInstance(ScourgeInvasionCatalog.DamagedNecroticShard,
                Me.X, Me.Y, Me.Z, Me.Orientation);
            return;
        }

        Creature? relay = System?.CreaturesOfEntryInRange(Me, ScourgeInvasionCatalog.NecropolisRelay, 200f)
            .Where(c => c.IsAlive).MinBy(c => InvasionCircleAi.DistanceSquared(Me, c));
        if (relay is not null) DoCast(relay, ScourgeInvasionCatalog.CampDeathCommunique, triggered: true);
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

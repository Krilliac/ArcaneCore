using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.WorldData.WorldState;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// PallidHorrorAI / ScourgeMinion MoveInLineOfSight: a capital defender (IsGuardOrBoss) within VISIBILITY_DISTANCE_TINY (25 yd) with no
/// victim of its own is told to attack the Scourge creature that saw it.
/// </summary>
internal static class ScourgeDefenders
{
    public const float Range = 25f;

    public static bool Call(Creature scourge, Unit who)
    {
        if (who is not Creature guard || !guard.IsAlive || guard.Combat.Victim is not null
            || !ScourgeInvasionCatalog.CityDefenders.Contains(guard.Entry) || guard.AI is null
            || InvasionCircleAi.DistanceSquared(scourge, guard) > Range * Range) return false;
        return guard.AI.AttackStart(scourge);
    }
}

internal static class ScourgeWeather
{
    /// <summary>vmangos Map::SetWeather, skipped where the map carries no weather updater.</summary>
    public static void Set(Creature creature, uint zone, WeatherType type, float grade, bool permanent)
        => creature.Map?.FindUpdater<MapWeather>()?.SetWeather(zone, type, grade, permanent);
}

/// <summary>
/// mangos-classic scourge_invasion.cpp MouthAI: passive; on the zone start a storm and one of two start yells, every
/// 150 s-1 h a random zone yell, and on the zone stop an end yell, clear weather and a forced despawn.
/// </summary>
internal sealed class ScourgeMouthAi(Creature creature, uint zoneId, Random random) : CreatureAI(creature)
{
    private bool _started;
    private uint _yellMs = NextYell(random);

    public uint ZoneId => zoneId;
    public bool Ended { get; private set; }

    private static uint NextYell(Random random) => (uint)random.Next(150_000, 3_600_001);

    public override bool AttackStart(Unit target) => false; // REACT_PASSIVE

    public override void MoveInLineOfSight(Unit who) { }

    public override void OnUpdate(uint diffMs)
    {
        if (Ended || System is not { } system) return;
        if (!_started)
        {
            _started = true;
            ScourgeWeather.Set(Me, zoneId, WeatherType.Storm, 0.25f, permanent: true);
            system.ZoneYell(Me, ScourgeInvasionCatalog.MouthZoneStartYells[random.Next(ScourgeInvasionCatalog.MouthZoneStartYells.Count)]);
            return;
        }

        if (_yellMs > diffMs)
        {
            _yellMs -= diffMs;
            return;
        }

        system.ZoneYell(Me, ScourgeInvasionCatalog.MouthRandomYells[random.Next(ScourgeInvasionCatalog.MouthRandomYells.Count)]);
        _yellMs = NextYell(random);
    }

    /// <summary>EVENT_MOUTH_OF_KELTHUZAD_ZONE_STOP.</summary>
    public void EndAttack()
    {
        if (Ended) return;
        Ended = true;
        if (System is not { } system) return;
        system.ZoneYell(Me, ScourgeInvasionCatalog.MouthZoneEndYells[random.Next(ScourgeInvasionCatalog.MouthZoneEndYells.Count)]);
        ScourgeWeather.Set(Me, zoneId, WeatherType.Rain, 0f, permanent: false);
        system.ForcedDespawn(Me, 0);
    }
}

/// <summary>
/// mangos-classic PallidHorrorAI (Pallid Horror 16394 and Patchwork Terror 16382): spawns with Aura of Fear and a ring of 5-9
/// following Flameshockers under a storm; in combat yells every 65-300 s, casts Damage vs Guards every 11-81 s and adds a
/// Flameshocker near a random attacker every 2 s up to 30; on death the capital leader yells, its Flameshockers die, it casts the
/// capital's necrotic-crystal summon, the weather clears and the next city attack is saved 45-60 minutes out.
/// </summary>
internal sealed class PallidHorrorAi(Creature creature, ScourgeInvasionFeature feature, uint zoneId) : CreatureAI(creature)
{
    public const int MaxFlameshockers = 30;
    private readonly List<Creature> _flameshockers = [];
    private readonly Dictionary<Creature, int> _followers = [];
    private bool _started;
    private uint _formationMs = 2_500;
    private uint _yellMs = 5_000;
    private uint _guardsMs = 5_000;
    private uint _summonMs = 5_000;

    public uint ZoneId => zoneId;
    public IReadOnlyList<Creature> Flameshockers => _flameshockers;
    private Random Random => feature.Random;

    public override bool AggroesOnSight => true;

    public override void MoveInLineOfSight(Unit who)
    {
        ScourgeDefenders.Call(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (System is not { } system || !Me.IsAlive) return;
        if (!_started)
        {
            _started = true;
            DoAddAura(ScourgeInvasionCatalog.AuraOfFear, permanent: true);
            int amount = Random.Next(5, 10); // sniffed groups of 5-9
            for (int i = 0; i < amount; i++)
            {
                if (system.SummonAt(Me, ScourgeInvasionCatalog.Flameshocker, Me.X, Me.Y, Me.Z, 0f, null, 3_600_000,
                        oocOrCorpse: true) is not { } shocker) continue;
                _flameshockers.Add(shocker);
                _followers[shocker] = i;
                Follow(shocker, i, amount);
            }
            ScourgeWeather.Set(Me, zoneId, WeatherType.Storm, 0.25f, permanent: true);
        }

        if (Tick(ref _formationMs, diffMs, 2_500))
        {
            foreach ((Creature follower, int index) in _followers)
                if (follower.IsAlive && !follower.Combat.IsInCombat && follower.Motion.CurrentType != MovementGeneratorType.Follow)
                    Follow(follower, index, _followers.Count);
        }

        UpdateVictim();
        if (!Me.Combat.IsInCombat || Victim is not { } victim) return;
        if (Tick(ref _yellMs, diffMs, (uint)Random.Next(65_000, 300_001)))
            system.ZoneYell(Me, ScourgeInvasionCatalog.PallidYells[Random.Next(ScourgeInvasionCatalog.PallidYells.Count)]);
        if (Tick(ref _guardsMs, diffMs, (uint)Random.Next(11_000, 81_001)))
            DoCast(victim, ScourgeInvasionCatalog.DamageVsGuards, triggered: true);
        if (Tick(ref _summonMs, diffMs, 2_000) && _flameshockers.Count < MaxFlameshockers)
            SummonNearAttacker(system);
    }

    private void Follow(Creature shocker, int index, int count)
    {
        float angle = index * (MathF.PI / (count / 2f)) + Me.Orientation;
        shocker.Motion.MoveFollow(Me, 2.5f, angle);
    }

    /// <summary>SelectRandomFlameshockerSpawnTarget, narrowed to this creature's attackers: one without a Flameshocker within 5 yd.</summary>
    private void SummonNearAttacker(CreatureMapSystem system)
    {
        Unit? target = SelectRandomAttackingTarget();
        if (target is null || _flameshockers.Any(f => f.IsAlive && DistanceSquared(f, target) <= 25f)) return;
        float angle = (float)(Random.NextDouble() * Math.PI * 2);
        if (system.SummonAt(Me, ScourgeInvasionCatalog.Flameshocker, target.X + 5f * MathF.Cos(angle), target.Y + 5f * MathF.Sin(angle),
                target.Z, target.Orientation, target, 3_600_000) is { } shocker)
        {
            _flameshockers.Add(shocker);
            (shocker.AI as FlameshockerAi)?.ArmDespawn();
        }
    }

    private static float DistanceSquared(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private static bool Tick(ref uint remaining, uint diffMs, uint reset)
    {
        if (remaining > diffMs)
        {
            remaining -= diffMs;
            return false;
        }
        remaining = reset;
        return true;
    }

    public override void OnSummonedCreatureJustDied(Creature summoned) => Forget(summoned);
    public override void OnSummonedCreatureDespawn(Creature summoned) => Forget(summoned);

    private void Forget(Creature summoned)
    {
        _flameshockers.Remove(summoned);
        _followers.Remove(summoned);
    }

    public override void OnDeath(Unit? killer)
    {
        if (System is { } system)
        {
            uint leader = zoneId == ScourgeInvasionCatalog.UndercityZone ? ScourgeInvasionCatalog.LadySylvanas : ScourgeInvasionCatalog.HighlordBolvar;
            int text = zoneId == ScourgeInvasionCatalog.UndercityZone
                ? ScourgeInvasionCatalog.SylvanasCourtDefended : ScourgeInvasionCatalog.BolvarCastleDefended;
            if (system.CreaturesOfEntryInRange(Me, leader, 100f).FirstOrDefault(c => c.IsAlive) is { } speaker)
                system.ZoneYell(speaker, text, Me);
            foreach (Creature shocker in _flameshockers.Where(f => f.IsAlive).ToArray())
                system.KillCreature(shocker);
            DoCast(Me, zoneId == ScourgeInvasionCatalog.UndercityZone
                ? ScourgeInvasionCatalog.SummonFaintNecroticCrystal : ScourgeInvasionCatalog.SummonCrackedNecroticCrystal, triggered: true);
        }
        _flameshockers.Clear();
        _followers.Clear();
        ScourgeWeather.Set(Me, zoneId, WeatherType.Rain, 0f, permanent: false);
        feature.OnCityAttackerDied(Me, zoneId);
    }
}

/// <summary>mangos-classic MinionAI for the Flameshocker (16383): immolate visual on spawn, a Touch every 30-45 s in combat, Revenge on death.</summary>
internal sealed class FlameshockerAi(Creature creature, Random random) : CreatureAI(creature)
{
    private bool _started;
    private uint _touchMs = 2_000;
    private uint _despawnMs; // EVENT_MINION_FLAMESHOCKERS_DESPAWN starts disabled; 0 is disarmed

    public override bool AggroesOnSight => true;

    /// <summary>
    /// AI_EVENT_CUSTOM_A with NPC_FLAMESHOCKER (scourge_invasion.cpp:1274-1282, 1051-1052): the Pallid Horror's attacker-side summon arms the
    /// 60 s despawn action; the escort ring does not.
    /// </summary>
    public void ArmDespawn() => _despawnMs = 60_000;

    /// <summary>Whether the 60 s despawn action is running (false for an escort-ring Flameshocker and after the action fired).</summary>
    public bool DespawnArmed => _despawnMs > 0;

    /// <summary>ScourgeMinion::SpellHit (:1069-1076): Spirit Spawn-out (17680) despawns it 3 s later.</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (spell.Id == ScourgeInvasionCatalog.SpiritSpawnOut) System?.ForcedDespawn(Me, 3_000);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        ScourgeDefenders.Call(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.IsAlive) return;
        if (!_started)
        {
            _started = true;
            DoCast(Me, ScourgeInvasionCatalog.MinionSpawnIn, triggered: true);
            DoCast(Me, ScourgeInvasionCatalog.FlameshockerImmolateVisual, triggered: true);
        }
        UpdateVictim();
        if (_despawnMs > 0)
        {
            // :1023-1029: out of combat cast Despawner, self (28091); in combat the action is re-armed for 60 s.
            if (_despawnMs > diffMs) _despawnMs -= diffMs;
            else if (Me.Combat.IsInCombat) _despawnMs = 60_000;
            else
            {
                _despawnMs = 0;
                DoCast(Me, ScourgeInvasionCatalog.DespawnerSelf, triggered: true);
            }
        }
        if (!Me.Combat.IsInCombat || Victim is not { } victim) return;
        if (_touchMs > diffMs)
        {
            _touchMs -= diffMs;
            return;
        }
        DoCast(victim, random.Next(2) == 0 ? ScourgeInvasionCatalog.FlameshockersTouch : ScourgeInvasionCatalog.FlameshockersTouch2, triggered: true);
        _touchMs = (uint)random.Next(30_000, 45_001);
    }

    public override void OnDeath(Unit? killer) => DoCast(Me, ScourgeInvasionCatalog.FlameshockersRevenge, triggered: true);
}

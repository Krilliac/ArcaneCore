using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Instances.Scripts.ZulFarrak;

/// <summary>ScriptDev2 boss_zumrahAI (mangos-classic zulfarrak/boss_zumrah.cpp:
/// Reset, MoveInLineOfSight, Aggro, JustSummoned, SelectNearbyShallowGrave, UpdateAI).</summary>
public sealed class ZumrahAi(Creature creature, ZulFarrakInstance instance) : ScriptedAI(creature)
{
    public const uint Entry = 7271, Zombie = 7286, DeadHero = 7276, SummonZombiesSpell = 10247;
    private uint _boltMs, _volleyMs, _wardMs, _healMs, _zombieMs;
    private bool _hostile;

    /// <summary>Reset (spawn and every evade): the timers and the 10 yd attack distance. Turning hostile is not undone here.</summary>
    protected override void Reset()
    {
        _boltMs = 1000;
        _volleyMs = (uint)Random.Shared.Next(6000, 30001);
        _wardMs = (uint)Random.Shared.Next(7000, 20001);
        _healMs = (uint)Random.Shared.Next(10000, 15001);
        _zombieMs = 1000;
        CasterChaseDistance = 10f;
    }

    /// <summary>
    /// A respawn also brings back the template faction and the intro. Deviation: boss_zumrah.cpp clears m_bHasTurnedHostile only in the
    /// constructor and its temporary faction carries no restore flag; here a respawned Zum'rah is the neutral one players first meet.
    /// </summary>
    public override void OnRespawn()
    {
        base.OnRespawn();
        _hostile = false;
        Me.FactionTemplate = Me.Template.Faction;
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (!_hostile && who is Player && who.IsAlive && DistanceSq(who, Me) <= 9 * 9 && Me.IsWithinLineOfSight(who))
        {
            _hostile = true;
            Me.FactionTemplate = 14;
            Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
            System?.SayText(Me, -1209000);
            AttackStart(who);
        }
        base.MoveInLineOfSight(who);
    }

    public override void OnAggro(Unit target) => System?.SayText(Me, -1209001);
    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1209002);
    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry is Zombie or DeadHero && Victim is { } victim) summoned.AI?.AttackStart(victim);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        if (_zombieMs > 0)
        {
            if (_zombieMs > diffMs) _zombieMs -= diffMs;
            else if (NearestUsableGrave() is { } grave)
            {
                // ClassicDB spell_template 10247 has two SUMMON_WILD effects (7286 and 7276).
                // The spell system uses its die sides and duration; no summon count is invented here.
                if (System?.CastSpellAtDestination(Me, SummonZombiesSpell, grave.X, grave.Y, grave.Z, triggered: true)
                    == CreatureCastResult.Ok)
                {
                    Me.Map?.FindUpdater<GameObjectMapSystem>()?.DespawnForRespawn(grave);
                    if (Random.Shared.Next(100) < 30) System?.SayText(Me, -1209003);
                    _zombieMs = 20_000;
                }
            }
            else _zombieMs = 0;
        }

        if (_boltMs >= diffMs) _boltMs -= diffMs;
        // SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 0) (boss_zumrah.cpp:165): any attacker, not only the tank.
        else if (SelectRandomAttackingTarget(0) is { } boltTarget && DoCast(boltTarget, 12739) == CreatureCastResult.Ok)
            _boltMs = (uint)Random.Shared.Next(3500, 5001);
        if (_volleyMs >= diffMs) _volleyMs -= diffMs;
        else if (DoCast(Me, 15245) == CreatureCastResult.Ok) _volleyMs = (uint)Random.Shared.Next(10000, 18001);
        if (_wardMs >= diffMs) _wardMs -= diffMs;
        else if (DoCast(Me, 11086) == CreatureCastResult.Ok) _wardMs = (uint)Random.Shared.Next(15000, 32001);
        if (_healMs >= diffMs) _healMs -= diffMs;
        // DoSelectLowestHpFriendly(40.0f) (boss_zumrah.cpp:192): the assistable creature in combat missing the most health.
        else if (SelectLowestHpFriendly(40f) is { } friend && DoCast(friend, 12491) == CreatureCastResult.Ok)
            _healMs = (uint)Random.Shared.Next(15000, 23001);
    }

    private GameObject? NearestUsableGrave()
    {
        GameObjectMapSystem? objects = Me.Map?.FindUpdater<GameObjectMapSystem>();
        return instance.ShallowGraves.Select(g => objects?.Find(g))
            .Where(go => go is { IsSpawned: true, LootState: GameObjectLootState.Ready })
            .OrderBy(go => DistanceSq(go!, Me)).FirstOrDefault();
    }

    private static float DistanceSq(WorldObject a, WorldObject b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}

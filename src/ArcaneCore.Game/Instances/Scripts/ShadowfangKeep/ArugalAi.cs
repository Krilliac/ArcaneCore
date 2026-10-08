using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>Arugal's spell cycle from mangos-classic shadowfang_keep/shadowfang_keep.cpp:388-569,
/// boss_arugalAI::Aggro/KilledUnit/ExecuteAction. Teleport spells choose one of the three ScriptDev2 positions.</summary>
public sealed class ArugalAi(Creature creature) : AggressorAI(creature)
{
    private uint _bolt, _shock, _curse, _teleport;
    private int _position;

    public override void OnRespawn()
    {
        _bolt = 1_000;
        _shock = 1_000;
        _curse = (uint)(System?.RandomInt(20_000, 30_000) ?? 20_000);
        _teleport = (uint)(System?.RandomInt(22_000, 26_000) ?? 22_000);
        _position = 0;
        CasterChaseDistance = 50f;
        MeleeEnabled = false;
        if (Me.Z < 140f) // the Fenrus-room Arugal is an untouchable event projection
        {
            Me.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.ImmuneToNpc | UnitFlags.NotSelectable;
        }
    }

    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, -1033017);
        DoCast(target, 7588);
    }

    public override void OnKilledUnit(Unit victim)
    {
        if (victim is Player)
        {
            System?.SayText(Me, -1033018);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (Elapsed(ref _bolt, diffMs) && DoCast(victim, 7588) == CreatureCastResult.Ok)
            _bolt = (uint)(System?.RandomInt(2_900, 4_800) ?? 3_500);

        if (Elapsed(ref _shock, diffMs) && MapCombat.CanReachWithMeleeAutoAttack(Me, victim)
            && DoCast(Me, 7803) == CreatureCastResult.Ok)
            _shock = (uint)(System?.RandomInt(30_000, 38_000) ?? 34_000);

        if (Elapsed(ref _curse, diffMs))
        {
            Unit[] otherAttackers = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
                .Where(u => u.IsAlive && !ReferenceEquals(u, victim))];
            if (otherAttackers.Length > 0)
            {
                Unit target = otherAttackers[System?.RandomInt(0, otherAttackers.Length - 1) ?? 0];
                System?.SayText(Me, -1033019);
                if (DoCast(target, 7621) == CreatureCastResult.Ok)
                    _curse = (uint)(System?.RandomInt(20_000, 35_000) ?? 27_000);
            }
        }

        if (Elapsed(ref _teleport, diffMs))
        {
            int next = System?.RandomInt(0, 2) ?? 0;
            if (next == _position && next != 1)
            {
                _teleport = (uint)(System?.RandomInt(10_000, 15_000) ?? 12_000);
            }
            else
            {
                uint spell = next switch { 0 => 7586u, 1 => 7587u, _ => 7136u };
                System?.InterruptCast(Me);
                CreatureCastResult result = DoCast(Me, spell);
                if (result != CreatureCastResult.Ok && _position == 1)
                {
                    result = DoCast(Me, spell, triggered: true); // out of mana on the upper ledge: forced, or the encounter is stuck
                }

                if (result == CreatureCastResult.Ok)
                {
                    _position = next;
                    MeleeEnabled = next != 1;
                    _teleport = (uint)(System?.RandomInt(next == 1 ? 2_000 : 48_000, next == 1 ? 2_200 : 55_000) ?? 50_000);
                }
            }
        }
    }

    private static bool Elapsed(ref uint timer, uint diffMs)
    {
        if (timer <= diffMs) return true;
        timer -= diffMs;
        return false;
    }
}

/// <summary>Arugal Voidwalker's Dark Offering and death count (mob_arugal_voidwalkerAI::ExecuteAction/JustDied,
/// shadowfang_keep.cpp:320-337). Formation path data is left to creature_movement_template.</summary>
public sealed class ArugalVoidwalkerAi(Creature creature, ShadowfangKeepInstance instance) : AggressorAI(creature)
{
    private uint _offering = 4_400;
    private uint _groupCheck = 1_000;
    private ObjectGuid _leaderGuid;

    public override void OnRespawn()
    {
        _offering = (uint)(System?.RandomInt(4_400, 12_500) ?? 4_400);
        _groupCheck = 1_000;
        _leaderGuid = default;
        CheckGroupStatus();
    }

    public override void OnDeath(Unit? killer) => instance.SetData(ShadowfangKeepInstance.TypeVoidwalker, EncounterState.Done);

    public override void OnUpdate(uint diffMs)
    {
        if (_groupCheck <= diffMs)
        {
            _groupCheck = 1_000;
            CheckGroupStatus();
        }
        else _groupCheck -= diffMs;

        if (!UpdateVictim()) return;
        if (_offering > diffMs)
        {
            _offering -= diffMs;
            return;
        }

        // DoSelectLowestHpFriendly(10, 290): a friendly missing at least 290 health.
        Creature? target = System?.Creatures.Where(c => c.IsAlive && c.FactionTemplate == Me.FactionTemplate
            && c.MaxHealth > c.Health && c.MaxHealth - c.Health >= 290
            && DistanceSquared(c, Me) <= 100f)
            .OrderByDescending(c => c.MaxHealth - c.Health).FirstOrDefault();
        if (target is not null && DoCast(target, 7154) == CreatureCastResult.Ok)
            _offering = (uint)(System?.RandomInt(4_000, 12_000) ?? 8_000);
    }

    private static float DistanceSquared(Creature a, Creature b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private void CheckGroupStatus()
    {
        if (System is not { } system || Me.Combat.IsInCombat)
        {
            return;
        }

        Creature[] walkers = [.. system.CreaturesOfEntryInRange(Me, 4627, 50f).Where(c => c.IsAlive).OrderBy(c => c.Guid.Value)];
        if (walkers.Length == 0)
        {
            return;
        }

        Creature leader = walkers[0];
        if (ReferenceEquals(leader, Me))
        {
            if (_leaderGuid != Me.Guid || Me.Motion.Top.Type != MovementGeneratorType.Waypoint)
            {
                _leaderGuid = Me.Guid;
                system.ChangeMovement(Me, 2, 0, 0); // mob_arugal_voidwalkerAI::SetLeader -> MoveWaypoint(0)
            }
        }
        else if (_leaderGuid != leader.Guid || Me.Motion.Top.Type != MovementGeneratorType.Follow)
        {
            _leaderGuid = leader.Guid;
            int position = Array.IndexOf(walkers, Me);
            Me.Motion.MoveFollow(leader, 1f, MathF.PI / 2 * position);
        }
    }
}

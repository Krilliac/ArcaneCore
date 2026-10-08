using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>
/// Ring of Law escort and waves: mangos-classic blackrock_depths/blackrock_depths.cpp
/// AreaTrigger_at_ring_of_law and npc_grimstoneAI (WaypointReached, JustSummoned,
/// SummonedCreatureJustDied, UpdateEscortAI). The escort reads Grimstone's imported waypoint path.
/// </summary>
public sealed class GrimstoneAI(Creature creature, BlackrockDepthsInstance instance) : EscortAI(creature)
{
    private static readonly uint[] RingMobs = [8925, 8926, 8927, 8928, 8933, 8932];
    private static readonly byte[] RingMobCounts = [4, 2, 5, 3, 3, 7];
    private static readonly uint[] RingBosses = [9027, 9028, 9029, 9030, 9031, 9032];
    private static readonly uint[] Gladiators = [16049, 16050, 16051, 16052, 16053, 16054, 16055, 16058];
    private readonly HashSet<ObjectGuid> _summons = [];
    private uint[] _chosenGladiators = [];
    private uint _eventMs = 1000;
    private int _eventPhase;
    private int _alive;
    private int _mobChoice;
    private RingPhase _ringPhase;

    private enum RingPhase { Mobs, Boss, Gladiators }

    protected override void Reset()
    {
        Me.UnitFlags |= UnitFlags.Spawning;
        _eventMs = 1000;
        _eventPhase = _alive = 0;
        _ringPhase = RingPhase.Mobs;
        _summons.Clear();
        _mobChoice = System?.RandomInt(0, 5) ?? 0;
        _chosenGladiators = [.. Gladiators.OrderBy(_ => System?.RandomInt(0, int.MaxValue) ?? 0).Take(4)];
    }

    public override void OnJustSummoned(Creature summoned)
    {
        _alive++;
        _summons.Add(summoned.Guid);
        var center = instance.ArenaCenter;
        summoned.Motion.MovePoint(1, center.X + (System?.RandomInt(-1000, 1000) ?? 0) / 100f,
            center.Y + (System?.RandomInt(-1000, 1000) ?? 0) / 100f, center.Z, run: true);
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (!_summons.Remove(summoned.Guid)) return;
        _alive = Math.Max(0, _alive - 1);
        if (_alive == 0) _eventMs = 5000;
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1: Say(-1230004); SetEscortPaused(true); _eventMs = 5000; break;
            case 2: Say(-1230006); SetEscortPaused(true); _eventMs = 5000; break;
            case 3: SetEscortPaused(true); break;
            case 4: Say(-1230007); break;
            case 5: Say(-1230009); SetEscortPaused(true); _eventMs = 5000; break;
            case 6: instance.SetData(BlackrockDepthsInstance.TypeRingOfLaw, EncounterState.Done); break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (instance.GetData(BlackrockDepthsInstance.TypeRingOfLaw) == EncounterState.Fail)
        {
            if (_eventPhase >= 10)
            {
                instance.UseArenaDoor(161522);
                instance.UseArenaDoor(161523);
            }
            else if (_eventPhase >= 4)
            {
                instance.UseArenaDoor(161525);
                instance.UseArenaDoor(161523);
            }
            foreach (ObjectGuid guid in _summons)
                if (System?.FindCreature(guid) is { } mob) System.ForcedDespawn(mob, 0);
            _summons.Clear();
            System?.ForcedDespawn(Me, 0);
            return;
        }
        if (_eventMs == 0) return;
        if (_eventMs > diffMs) { _eventMs -= diffMs; return; }
        switch (_eventPhase)
        {
            case 0:
                DoCast(Me, 15742, triggered: true);
                Say(-1230005);
                instance.UseArenaDoor(161523);
                // Some of the crowd cheers at the start: urand(0, 3) < 1 picks about a quarter (EMOTE_ONESHOT_CHEER).
                foreach (ObjectGuid guid in instance.ArenaCrowdGuids)
                    if (System?.FindCreature(guid) is { IsAlive: true } spectator && System.RandomInt(0, 3) < 1)
                        System.PlayEmote(spectator, 4);
                Start(run: false);
                SetEscortPaused(false);
                _eventMs = 0;
                break;
            case 1: SetEscortPaused(false); _eventMs = 0; break;
            case 2: _eventMs = 2000; break;
            case 3:
                DoCast(Me, 15737, triggered: true);
                DoCast(Me, 15739, triggered: true);
                instance.UseArenaDoor(161525);
                _eventMs = 3000;
                break;
            case 4: DoCast(Me, 6422, triggered: true); _eventMs = 2500; break;
            case 5:
                SetEscortPaused(false);
                SummonRingWave();
                _eventMs = 16_000;
                break;
            case 7: SummonRingWave(); _eventMs = 0; break;
            case 8:
                Say(-1230008);
                DoCast(Me, 15742, triggered: true);
                instance.UseArenaDoor(161525);
                SetEscortPaused(false);
                _eventMs = 0;
                break;
            case 9:
                DoCast(Me, 15740, triggered: true);
                DoCast(Me, 15741, triggered: true);
                instance.UseArenaDoor(161522);
                _eventMs = 5000;
                break;
            case 10: DoCast(Me, 6422, triggered: true); _eventMs = 2500; break;
            case 11:
                if (instance.GetData(BlackrockDepthsInstance.TypeRingOfLaw) == EncounterState.Special && _ringPhase == RingPhase.Mobs)
                {
                    _ringPhase = RingPhase.Gladiators;
                    SummonAtNorth(16059);
                    foreach (uint entry in _chosenGladiators) SummonAtNorth(entry);
                }
                else
                {
                    _ringPhase = RingPhase.Boss;
                    SummonAtNorth(RingBosses[System?.RandomInt(0, 5) ?? 0]);
                }
                _eventMs = 0;
                break;
            case 12:
                _summons.Clear();
                instance.UseArenaDoor(161522);
                instance.UseArenaDoor(161524);
                instance.UseArenaDoor(161523);
                SetEscortPaused(false);
                _eventMs = 0;
                break;
            default: _eventMs = 0; break;
        }
        _eventPhase++;
    }

    private void SummonRingWave()
    {
        uint entry = RingMobs[_mobChoice];
        for (int i = 0; i < RingMobCounts[_mobChoice]; i++)
            System?.SummonCorpseDespawn(Me, entry, 608.960f, -235.322f, -53.907f, 1.857f);
    }

    private void SummonAtNorth(uint entry)
        => System?.SummonCorpseDespawn(Me, entry, 644.300f, -175.989f, -53.739f, 3.418f);

    private void Say(int text) => System?.SayText(Me, text);
}

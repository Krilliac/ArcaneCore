using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Eris Havenfire (entry 14494, Eastern Plaguelands), the priest quest 7622 "The Balance of Light and Shadow": mangos-classic ScriptDev2
/// <c>npc_eris_havenfire</c> (eastern_kingdoms/eastern_plaguelands.cpp). Five seconds after accept eight Scourge Archers appear, then five
/// waves of 11, 12, ... peasants (70% injured, 30% plagued) walk to the light while footsoldiers keep coming; a wave ends when all its
/// peasants are saved or dead (Blessing of Nordrassil on the player, next wave). Fifty saved is the credit; fifteen dead fails the quest.
/// SD2's SummonedMovementInform and SummonedCreatureJustDied are read here from the summons each update (arrival within a yard of the
/// peasant's point, death). The player's own Blessing cast is Eris casting it triggered on the player.
/// </summary>
public sealed class ErisHavenfireAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint Entry = 14494, QuestBalanceOfLightAndShadow = 7622;
    public const uint NpcInjuredPeasant = 14484, NpcPlaguedPeasant = 14485, NpcScourgeArcher = 14489, NpcScourgeFootsoldier = 14486;
    public const uint SpellEnterTheLight = 23107, SpellBlessingOfNordrassil = 23108;
    public const int SayPhaseHeal = -1000815, SayEventEnd = -1000816, SayEventFail1 = -1000817, SayEventFail2 = -1000818;
    public static readonly int[] SayPeasantAppear = [-1000819, -1000820, -1000821];
    public const int BasePeasantsPerWave = 11, MaxKilledPeasant = 15, MaxSavedPeasant = 50;

    private static readonly (float X, float Y, float Z, float O)[] s_archerSpawn =
    [
        (3327.42f, -3021.11f, 170.57f, 6.01f), (3335.4f, -3054.3f, 173.63f, 0.49f), (3351.3f, -3079.08f, 178.67f, 1.15f),
        (3358.93f, -3076.1f, 174.87f, 1.57f), (3371.58f, -3069.24f, 175.20f, 1.99f), (3369.46f, -3023.11f, 171.83f, 3.69f),
        (3383.25f, -3057.01f, 181.53f, 2.21f), (3380.03f, -3062.73f, 181.90f, 2.31f),
    ];
    private static readonly (float X, float Y, float Z) s_peasantSpawn = (3360.12f, -3047.79f, 165.26f);
    private static readonly (float X, float Y, float Z) s_peasantMove = (3335.0f, -2994.04f, 161.14f);

    private uint _eventMs, _sadEndMs, _archerCheckMs;
    private int _phase, _currentWave, _killCounter, _saveCounter, _totalCounter;
    private ObjectGuid _playerGuid;
    private readonly List<Creature> _summons = [];
    private readonly Dictionary<Creature, (float X, float Y, float Z)> _walking = new(ReferenceEqualityComparer.Instance);

    public int Saved => _saveCounter;
    public int Killed => _killCounter;
    public int Wave => _currentWave;
    public IReadOnlyList<Creature> Summons => _summons;

    public override void OnRespawn() => Reset();

    public override void OnEvade() => Reset();

    private void Reset()
    {
        _eventMs = _sadEndMs = _archerCheckMs = 0;
        _phase = _currentWave = _killCounter = _saveCounter = _totalCounter = 0;
        _playerGuid = default;
        _summons.Clear();
        _walking.Clear();
        Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestBalanceOfLightAndShadow)
        {
            return;
        }

        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        _playerGuid = player.Guid;
        _eventMs = 5000;
        _archerCheckMs = 2000;
    }

    private Player? EventPlayer => System?.Map.FindPlayer(_playerGuid);

    private (float X, float Y, float Z) RandomPoint((float X, float Y, float Z) centre, float radius, CreatureMapSystem system)
    {
        float angle = system.RandomInt(0, 35_999) * (MathF.PI / 18_000f);
        float distance = radius * MathF.Sqrt(system.RandomInt(0, 10_000) / 10_000f);
        return (centre.X + (distance * MathF.Cos(angle)), centre.Y + (distance * MathF.Sin(angle)), centre.Z);
    }

    private Creature? Summon(uint entry, float x, float y, float z, float o)
    {
        if (System is not { } system || system.SummonAt(Me, entry, x, y, z, o, null, 0) is not { } summoned)
        {
            return null;
        }

        _summons.Add(summoned);
        switch (entry)
        {
            case NpcInjuredPeasant or NpcPlaguedPeasant:
                (float mx, float my, float mz) = RandomPoint(s_peasantMove, 10f, system);
                _walking[summoned] = (mx, my, mz);
                system.MoveTo(summoned, mx, my, mz, run: false, finalOrientation: null);
                _totalCounter++;
                break;
            case NpcScourgeFootsoldier:
                if (EventPlayer is { } player)
                {
                    system.AttackStart(summoned, player);
                }

                break;
        }

        return summoned;
    }

    private void DoSummonWave(uint summonId = 0)
    {
        if (System is not { } system)
        {
            return;
        }

        if (summonId == 0)
        {
            for (int i = 0; i < BasePeasantsPerWave + _currentWave; i++)
            {
                uint entry = system.RandomInt(0, 99) < 70 ? NpcInjuredPeasant : NpcPlaguedPeasant;
                (float x, float y, float z) = RandomPoint(s_peasantSpawn, 10f, system);
                if (Summon(entry, x, y, z, 0f) is { } peasant && i == 0)
                {
                    system.SayText(peasant, SayPeasantAppear[system.RandomInt(0, 2)]);
                }
            }

            _currentWave++;
        }
        else if (summonId == NpcScourgeFootsoldier)
        {
            int count = system.RandomInt(2, 3);
            for (int i = 0; i < count; i++)
            {
                (float x, float y, float z) = RandomPoint(s_peasantSpawn, 15f, system);
                Summon(NpcScourgeFootsoldier, x, y, z, 0f);
            }
        }
        else if (summonId == NpcScourgeArcher)
        {
            foreach ((float x, float y, float z, float o) in s_archerSpawn)
            {
                Summon(NpcScourgeArcher, x, y, z, o);
            }
        }
    }

    private void DoHandlePhaseEnd()
    {
        if (EventPlayer is { } player)
        {
            DoCast(player, SpellBlessingOfNordrassil, triggered: true);
        }

        System?.SayText(Me, SayPhaseHeal);
        if (_currentWave < 5)
        {
            DoSummonWave();
        }
    }

    private void DoBalanceEventEnd()
    {
        if (EventPlayer is { } player)
        {
            System?.QuestEventHappened(player, QuestBalanceOfLightAndShadow);
        }

        System?.SayText(Me, SayEventEnd);
        _archerCheckMs = 0;
        DoDespawnSummons(eventEnd: true);
        EnterEvadeMode();
    }

    private void DoDespawnSummons(bool eventEnd = false)
    {
        foreach (Creature summoned in _summons.ToArray())
        {
            if (eventEnd && summoned.Entry is NpcInjuredPeasant or NpcPlaguedPeasant)
            {
                continue;
            }

            System?.ForcedDespawn(summoned, 0);
        }
    }

    /// <summary>SummonedMovementInform and SummonedCreatureJustDied for the peasants.</summary>
    private void WatchPeasants()
    {
        foreach ((Creature peasant, (float X, float Y, float Z) target) in _walking.ToArray())
        {
            if (!peasant.IsAlive)
            {
                _walking.Remove(peasant);
                ++_killCounter;
                if (_killCounter == MaxKilledPeasant)
                {
                    if (EventPlayer is { } player)
                    {
                        System?.QuestFailed(player, QuestBalanceOfLightAndShadow);
                    }

                    System?.SayText(Me, SayEventFail1);
                    _sadEndMs = 4000;
                }
                else if (_saveCounter + _killCounter == _totalCounter)
                {
                    DoHandlePhaseEnd();
                }

                continue;
            }

            float dx = peasant.X - target.X, dy = peasant.Y - target.Y;
            if ((dx * dx) + (dy * dy) > 1f)
            {
                continue;
            }

            _walking.Remove(peasant);
            ++_saveCounter;
            System?.CastSpell(peasant, SpellEnterTheLight, peasant, false);
            System?.ForcedDespawn(peasant, 10_000);
            if (_saveCounter >= MaxSavedPeasant)
            {
                DoBalanceEventEnd();
                return;
            }

            if (_saveCounter + _killCounter == _totalCounter)
            {
                DoHandlePhaseEnd();
            }
        }
    }

    private void DoAttackArchersTarget()
    {
        if (System is not { } system)
        {
            return;
        }

        foreach (Creature archer in _summons.Where(c => c.Entry == NpcScourgeArcher && c.IsAlive))
        {
            if (archer.Combat.Victim is { } victim && Within(archer, victim, 30f))
            {
                continue;
            }

            Creature? target = Closest(archer, NpcInjuredPeasant) ?? Closest(archer, NpcPlaguedPeasant);
            if (target is not null)
            {
                system.AttackStart(archer, target);
            }
        }
    }

    private Creature? Closest(Creature from, uint entry) => _summons
        .Where(c => c.IsAlive && c.Entry == entry && Within(from, c, 30f))
        .OrderBy(c => ((c.X - from.X) * (c.X - from.X)) + ((c.Y - from.Y) * (c.Y - from.Y))).FirstOrDefault();

    private static bool Within(WorldObject a, WorldObject b, float range)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= range * range;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_eventMs != 0)
        {
            if (_eventMs <= diffMs)
            {
                switch (_phase)
                {
                    case 0:
                        DoSummonWave(NpcScourgeArcher);
                        _eventMs = 5000;
                        break;
                    case 1:
                        DoSummonWave();
                        _eventMs = (uint)(System?.RandomInt(60_000, 80_000) ?? 60_000);
                        break;
                    default:
                        DoSummonWave(NpcScourgeFootsoldier);
                        _eventMs = (uint)(System?.RandomInt(5_000, 30_000) ?? 5_000);
                        break;
                }

                ++_phase;
            }
            else
            {
                _eventMs -= diffMs;
            }
        }

        if (_walking.Count != 0)
        {
            WatchPeasants();
        }

        if (_sadEndMs != 0)
        {
            if (_sadEndMs <= diffMs)
            {
                System?.SayText(Me, SayEventFail2);
                System?.ForcedDespawn(Me, 5000);
                DoDespawnSummons();
                _sadEndMs = 0;
            }
            else
            {
                _sadEndMs -= diffMs;
            }
        }

        if (_archerCheckMs != 0)
        {
            if (_archerCheckMs <= diffMs)
            {
                DoAttackArchersTarget();
                _archerCheckMs = 2000;
            }
            else
            {
                _archerCheckMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

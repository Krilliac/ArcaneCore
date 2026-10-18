using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Witch Doctor Unbagwa (entry 1449, Stranglethorn Vale), quest 349 "Stranglethorn Fever": vmangos <c>npc_witch_doctor_unbagwa</c>
/// (scripts/eastern_kingdoms/stranglethorn_vale/stranglethorn_vale.cpp:203-356). Turning the quest in starts the ape waves: he stops
/// offering quests, turns escort-passive (faction 113), and ten seconds after each wave is down summons the next one at the clearing below
/// him: three Enraged Silverback Gorillas (1511), then four and Konda (1516), then five and Mokk the Savage (1514), each attacking him. When
/// the last wave dies, or a living attacker despawns (its 3 minute out-of-combat timer ran out) and the rest are gone, he resets.
/// cmangos does this with spawn groups 18011-18013 that classic-db z2815 does not have, so the vmangos script is the one ported.
/// </summary>
public sealed class WitchDoctorUnbagwaAI(Creature creature) : CreatureAI(creature)
{
    public const uint Entry = 1449, QuestStranglethornFever = 349, NpcEnragedSilverbackGorilla = 1511, NpcKonda = 1516, NpcMokkTheSavage = 1514;
    public const uint FactionEscortNeutralPassive = 113, MaxWaveCount = 3, WaveDelayMs = 10_000, SummonLifetimeMs = 180_000;
    private const float SummonX = -13773.6231f, SummonY = -3.8856f, SummonZ = 41.5641f, SummonO = 5.7f;

    private uint _waveCount = 1, _attackersCount, _waveTimerMs = WaveDelayMs;
    private bool _eventStarted, _resetEvent;

    public bool EventStarted => _eventStarted;
    public uint WaveCount => _waveCount;
    public uint AttackersLeft => _attackersCount;

    public override void OnRespawn() => ResetEvent();

    private void ResetEvent()
    {
        _eventStarted = false;
        _resetEvent = false;
        _waveCount = 1;
        _attackersCount = 0;
        _waveTimerMs = WaveDelayMs;
        Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
        Me.ClearTemporaryFaction();
    }

    public override void OnQuestRewarded(Player player, uint questId)
    {
        base.OnQuestRewarded(player, questId);
        if (questId != QuestStranglethornFever || _eventStarted)
        {
            return;
        }

        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        Me.SetFactionTemporary(FactionEscortNeutralPassive, TemporaryFactionFlags.None);
        _eventStarted = true;
        _resetEvent = false;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        base.OnJustSummoned(summoned);
        if (_eventStarted)
        {
            summoned.AI?.AttackStart(Me);
        }
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        base.OnSummonedCreatureDespawn(summoned);
        if (!_eventStarted || !summoned.IsAlive)
        {
            return;
        }

        _resetEvent = true;
        if (_attackersCount > 0)
        {
            --_attackersCount;
        }

        if (_attackersCount == 0)
        {
            ResetEvent();
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        base.OnSummonedCreatureJustDied(summoned);
        if (!_eventStarted)
        {
            return;
        }

        if (_attackersCount > 0)
        {
            --_attackersCount;
        }

        if (_attackersCount == 0 && (_resetEvent || _waveCount > MaxWaveCount))
        {
            ResetEvent();
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_eventStarted && _attackersCount == 0 && System is { } system)
        {
            if (_waveTimerMs < diffMs)
            {
                _attackersCount = _waveCount switch { 1 => 3u, 2 => 5u, _ => 6u };
                uint count = _attackersCount;
                for (uint i = 0; i < count; i++)
                {
                    uint entry = NpcEnragedSilverbackGorilla;
                    if (_waveCount > 1 && i == count - 1)
                    {
                        entry = _waveCount == 2 ? NpcKonda : NpcMokkTheSavage;
                    }

                    float x = SummonX + (system.RandomInt(-300, 300) / 100f), y = SummonY + (system.RandomInt(-300, 300) / 100f);
                    // TEMPSUMMON_TIMED_OR_DEAD_DESPAWN, 3 minutes. A missing template leaves one fewer attacker to wait for.
                    if (system.SummonAt(Me, entry, x, y, SummonZ, SummonO, Me, SummonLifetimeMs) is null && _attackersCount > 0)
                    {
                        --_attackersCount;
                    }
                }

                _waveTimerMs = WaveDelayMs;
                ++_waveCount;
            }
            else
            {
                _waveTimerMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

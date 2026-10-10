using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Daphne Stilwell (entry 6182, Westfall), "The Tome of Valor" (quest 1651): mangos-classic ScriptDev2
/// <c>npc_daphne_stilwell</c> (eastern_kingdoms/westfall.cpp at 8ec338a). She runs to her hut, takes her rifle and holds at point 7 while three
/// waves of Defias Raiders (3, 4 and 5, 50 s apart from point 4) come down the hill; once the last wave is dead she walks on, and point 16
/// credits the group. She shoots (spell 6660, every 2 s) without chasing.
/// Not ported: the rifle equip and sheath swap (no script equipment API here).
/// </summary>
public sealed class DaphneStilwellAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 6182, QuestTomeOfValor = 1651, NpcDefiasRaider = 6180, SpellShoot = 6660;
    public const int YellDefiasStart = -1000412, YellDaphneStart = -1000413, SayStart = -1000293, SayDown3 = -1000296, SayPrologue = -1000297;
    private static readonly int[] s_waveDown = [-1000294, -1000295, -1000414];
    private const uint EmoteOneshotCheer = 4, EmoteOneshotRoar = 15, EmoteStateStand = 26, EmoteStateUseStanding = 69, EmoteStateUseStandingNoSheathe = 133;
    private const uint ShootCooldownMs = 2000, WaveIntervalMs = 50_000;

    private static readonly (float X, float Y, float Z)[] s_raider =
    [
        (-11428.520f, 1612.757f, 72.241f), (-11422.998f, 1616.106f, 74.153f), (-11430.354f, 1618.334f, 72.632f),
        (-11423.307f, 1621.033f, 74.224f), (-11427.141f, 1623.220f, 73.168f),
        (-11453.118f, 1554.380f, 53.100f), (-11449.692f, 1554.672f, 53.598f), (-11454.533f, 1558.679f, 52.497f),
        (-11449.488f, 1557.817f, 53.443f), (-11452.123f, 1559.800f, 52.890f),
        (-11475.067f, 1534.259f, 50.199f), (-11470.306f, 1533.835f, 50.267f), (-11471.954f, 1539.599f, 50.273f),
        (-11465.560f, 1534.399f, 50.649f), (-11467.391f, 1537.989f, 50.726f),
    ];

    private readonly List<(Creature Raider, int Slot, bool SecondLeg)> _raiders = [];
    private uint _shootMs, _waveMs;
    private int _wave;
    private bool _introDone;

    public int Wave => _wave;
    public IReadOnlyList<Creature> Raiders => [.. _raiders.Select(r => r.Raider)];

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestTomeOfValor)
        {
            return;
        }

        System?.SayText(Me, SayStart);
        Start(run: true, player: player, questId: questId);
    }

    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting))
        {
            return;
        }

        _introDone = false;
        _shootMs = 0;
        _waveMs = 0;
        CombatMovement = true;
    }

    protected override void Aggro(Unit target) => CombatMovement = false;

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 4:
                _wave = 0;
                _waveMs = 1000;
                System?.PlayEmote(Me, EmoteStateUseStandingNoSheathe);
                break;
            case 5:
                System?.PlayEmote(Me, EmoteStateStand);
                break;
            case 7:
                SetRun(false);
                System?.SayText(Me, YellDaphneStart);
                CombatMovement = false;
                SetEscortPaused(true);
                break;
            case 8:
                System?.SayText(Me, SayDown3);
                System?.PlayEmote(Me, EmoteOneshotCheer);
                break;
            case 9:
                System?.SayText(Me, SayPrologue);
                CombatMovement = true;
                break;
            case 12:
                System?.PlayEmote(Me, EmoteStateUseStanding);
                break;
            case 13:
                System?.PlayEmote(Me, EmoteStateStand);
                break;
            case 16:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestTomeOfValor, Me);
                }

                break;
        }
    }

    private void DoSendWave()
    {
        if (_wave > 2 || System is not { } system)
        {
            return;
        }

        ++_wave;
        for (int slot = 0; slot < _wave + 2; slot++)
        {
            (float x, float y, float z) = s_raider[slot];
            // TEMPSPAWN_TIMED_OOC_DESPAWN, 30000.
            if (system.SummonAt(Me, NpcDefiasRaider, x, y, z, 0f, null, 30_000) is not { } raider)
            {
                continue;
            }

            _raiders.Add((raider, slot, false));
            (float mx, float my, float mz) = s_raider[5 + slot];
            system.MoveTo(raider, mx, my, mz, run: true, finalOrientation: null);
            if (!_introDone)
            {
                system.SayText(raider, YellDefiasStart);
                _introDone = true;
            }

            if (slot == 0)
            {
                system.PlayEmote(raider, EmoteOneshotRoar);
            }
        }

        _waveMs = _wave < 3 ? WaveIntervalMs : 0;
    }

    /// <summary>SummonedMovementInform: from the first point each raider walks on to the second, then idles.</summary>
    private void WatchRaiders()
    {
        if (System is not { } system)
        {
            return;
        }

        bool any = _raiders.Count > 0;
        for (int i = _raiders.Count - 1; i >= 0; i--)
        {
            (Creature raider, int slot, bool second) = _raiders[i];
            if (!raider.IsAlive || !raider.IsInWorld)
            {
                _raiders.RemoveAt(i);
                continue;
            }

            (float tx, float ty, float _) = s_raider[5 + slot];
            if (!second && raider.Combat.Victim is null && MathF.Abs(raider.X - tx) < 1f && MathF.Abs(raider.Y - ty) < 1f)
            {
                (float mx, float my, float mz) = s_raider[10 + slot];
                system.MoveTo(raider, mx, my, mz, run: true, finalOrientation: null);
                _raiders[i] = (raider, slot, true);
            }
        }

        if (any && _raiders.Count == 0)
        {
            // SummonedCreatureJustDied with the list empty.
            if (_wave == 3)
            {
                SetEscortPaused(false);
                _wave = 0;
            }
            else
            {
                system.SayText(Me, s_waveDown[system.RandomInt(0, 2)]);
            }
        }
    }

    public override void OnDeath(Unit? killer)
    {
        _raiders.Clear();
        base.OnDeath(killer);
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_waveMs != 0)
        {
            if (_waveMs <= diffMs)
            {
                _waveMs = 0;
                DoSendWave();
            }
            else
            {
                _waveMs -= diffMs;
            }
        }

        WatchRaiders();
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_shootMs <= diffMs)
        {
            if (DoCast(victim, SpellShoot) == CreatureCastResult.Ok)
            {
                _shootMs = ShootCooldownMs;
            }
        }
        else
        {
            _shootMs -= diffMs;
        }
    }
}

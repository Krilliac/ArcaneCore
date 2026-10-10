using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Grark Lorkrub (entry 9520, Burning Steppes), "A Precarious Predicament" (quest 4121): mangos-classic ScriptDev2 <c>npc_grark_lorkrub</c>
/// and <c>EffectDummyCreature_spell_capture_grark</c> (eastern_kingdoms/burning_steppes.cpp at 3e8597afe7, the version that matches the
/// ClassicDB z2815 script_waypoint path). Capture Grark (spell 14250) at 25% health or less makes him submit (faction 35) and evade. The escort
/// is not interrupted by anything it sees. It pauses for three ambushes, at 12 (4 orcs), 24 (2 orcs and 2 dragonspawn) and 30 (the 3 Searscale
/// Drakes summoned at 28); each resumes after 4, 8 and 11 summon deaths. At 45 Nuzark and the Shadow of Lexlort appear for the execution
/// dialogue. Grark kneels, then lies as dead; on Lexlort's last line the group gets credit and Grark dies.
/// </summary>
public sealed class GrarkLorkrubAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 9520, QuestPrecariousPredicament = 4121, SpellCaptureGrark = 14250, FactionFriendly = 35;
    public const uint NpcBlackrockAmbusher = 9522, NpcBlackrockRaider = 9605, NpcFlamescaleDragonspawn = 7042, NpcSearscaleDrake = 7046;
    public const uint NpcNuzark = 9538, NpcLexlort = 9539;
    public const int SayStart = -1000873, SayPay = -1000874, SayFirstAmbushStart = -1000875, SayFirstAmbushEnd = -1000876,
        SaySecAmbushStart = -1000877, SaySecAmbushEnd = -1000878, SayThirdAmbushStart = -1000879, SayThirdAmbushEnd = -1000880,
        EmoteLaugh = -1000881, SayLastStand = -1000882, SayLexlort1 = -1000883, SayLexlort2 = -1000884, EmoteRaiseAxe = -1000885,
        EmoteLowerHand = -1000886, SayLexlort3 = -1000887, SayLexlort4 = -1000888, EmoteSubmit = -1000889, SayAggro = -1000890;
    private const uint EmoteOneshotAttack2hTight = 38;

    /// <summary>aOutroDialogue: text (or 0 for Grark's fake death), speaker, delay before the next step.</summary>
    private static readonly (int Text, uint Speaker, uint DelayMs)[] s_outro =
    [
        (SayLastStand, Entry, 5000), (SayLexlort1, NpcLexlort, 3000), (SayLexlort2, NpcLexlort, 5000), (EmoteRaiseAxe, NpcNuzark, 4000),
        (EmoteLowerHand, NpcLexlort, 3000), (SayLexlort3, NpcLexlort, 3000), (0, Entry, 5000), (SayLexlort4, NpcLexlort, 0),
    ];

    private readonly List<Creature> _searscales = [];
    private Creature? _nuzark, _lexlort;
    private int _killed, _outroStep = -1;
    private uint _outroMs;
    private bool _firstSearscale = true;

    /// <summary>45 is his last point: he holds there (paused) for the execution scene.</summary>
    protected override bool HoldAtEndWhilePaused => true;

    public int Killed => _killed;
    public bool Submitted => Me.FactionTemplate == FactionFriendly;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestPrecariousPredicament)
        {
            Start(run: false, player: player, questId: questId);
        }
    }

    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting))
        {
            return;
        }

        _killed = 0;
        _firstSearscale = true;
        _searscales.Clear();
        _outroStep = -1;
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
    }

    protected override void Aggro(Unit target)
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            System?.SayText(Me, SayAggro);
        }
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            base.MoveInLineOfSight(who);
        }
    }

    /// <summary>EffectDummyCreature_spell_capture_grark: only at 25% health or less.</summary>
    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellCaptureGrark)
        {
            Capture();
        }
    }

    public bool Capture()
    {
        if ((ulong)Me.Health * 100 > (ulong)Me.MaxHealth * 25)
        {
            return false;
        }

        System?.SayText(Me, EmoteSubmit);
        Me.FactionTemplate = FactionFriendly;
        EnterEvadeMode();
        return true;
    }

    private Creature? Summon(uint entry, float x, float y, float z, float o, uint despawnMs, bool timed = false)
    {
        if (System is not { } system)
        {
            return null;
        }

        // TEMPSPAWN_TIMED_OOC_DESPAWN (or TEMPSPAWN_TIMED_DESPAWN for the outro pair).
        // The outro pair never fights, so the out-of-combat timer is their TEMPSPAWN_TIMED_DESPAWN.
        _ = timed;
        Creature? summoned = system.SummonAt(Me, entry, x, y, z, o, null, despawnMs);
        if (summoned is null)
        {
            return null;
        }

        switch (entry)
        {
            case NpcNuzark:
                _nuzark = summoned;
                break;
            case NpcLexlort:
                _lexlort = summoned;
                break;
            case NpcSearscaleDrake:
                if (_firstSearscale)
                {
                    _firstSearscale = false; // the first one flies (SetLevitate, not ported; its circling is a ToDo in the source too)
                }

                _searscales.Add(summoned);
                break;
            default:
                if (GetPlayerForEscort() is { } player)
                {
                    system.AttackStart(summoned, player);
                }

                break;
        }

        return summoned;
    }

    protected override void WaypointReached(uint pointId)
    {
        if (System is not { } system)
        {
            return;
        }

        switch (pointId)
        {
            case 1:
                system.SayText(Me, SayStart);
                break;
            case 7:
                system.SayText(Me, SayPay);
                break;
            case 12:
                system.SayText(Me, SayFirstAmbushStart);
                SetEscortPaused(true);
                Summon(NpcBlackrockAmbusher, -7844.3f, -1521.6f, 139.2f, 0.0f, 20_000);
                Summon(NpcBlackrockAmbusher, -7860.4f, -1507.8f, 141.0f, 6.0f, 20_000);
                Summon(NpcBlackrockRaider, -7845.6f, -1508.1f, 138.8f, 6.1f, 20_000);
                Summon(NpcBlackrockRaider, -7859.8f, -1521.8f, 139.2f, 6.2f, 20_000);
                break;
            case 24:
                system.SayText(Me, SaySecAmbushStart);
                SetEscortPaused(true);
                Summon(NpcBlackrockAmbusher, -8035.3f, -1222.2f, 135.5f, 5.1f, 20_000);
                Summon(NpcFlamescaleDragonspawn, -8037.5f, -1216.9f, 135.8f, 5.1f, 20_000);
                Summon(NpcBlackrockAmbusher, -8009.5f, -1222.1f, 139.2f, 3.9f, 20_000);
                Summon(NpcFlamescaleDragonspawn, -8007.1f, -1219.4f, 140.1f, 3.9f, 20_000);
                break;
            case 28:
                Summon(NpcSearscaleDrake, -7897.8f, -1123.1f, 233.4f, 3.0f, 60_000);
                Summon(NpcSearscaleDrake, -7898.8f, -1125.1f, 193.9f, 3.0f, 60_000);
                Summon(NpcSearscaleDrake, -7895.6f, -1119.5f, 194.5f, 3.1f, 60_000);
                break;
            case 30:
                system.SayText(Me, SayThirdAmbushStart);
                if (_killed >= 11)
                {
                    // The drakes summoned at 28 already died on the way: the source would wait here forever.
                    system.SayText(Me, SayThirdAmbushEnd);
                    break;
                }

                SetEscortPaused(true);
                if (GetPlayerForEscort() is { } player)
                {
                    foreach (Creature drake in _searscales.Where(c => c.IsAlive && c.IsInWorld))
                    {
                        system.AttackStart(drake, player);
                    }
                }

                break;
            case 36:
                system.SayText(Me, EmoteLaugh);
                break;
            case 45:
                StartOutro();
                SetEscortPaused(true);
                Summon(NpcNuzark, -7532.3f, -1029.4f, 258.0f, 2.7f, 40_000, timed: true);
                Summon(NpcLexlort, -7532.8f, -1032.9f, 258.2f, 2.5f, 40_000, timed: true);
                break;
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ++_killed;
        int line = _killed switch { 4 => SayFirstAmbushEnd, 8 => SaySecAmbushEnd, 11 => SayThirdAmbushEnd, _ => 0 };
        if (line != 0)
        {
            System?.SayText(Me, line);
            SetEscortPaused(false);
        }

        base.OnSummonedCreatureJustDied(summoned);
    }

    private void StartOutro()
    {
        _outroStep = 0;
        DoOutroStep();
    }

    private void DoOutroStep()
    {
        (int text, uint speaker, uint delay) = s_outro[_outroStep];
        Creature? who = speaker switch { Entry => Me, NpcNuzark => _nuzark, NpcLexlort => _lexlort, _ => null };
        if (text != 0 && who is not null)
        {
            System?.SayText(who, text);
        }

        // JustDidDialogueStep.
        switch (text)
        {
            case SayLexlort1:
                Me.StandState = StandState.Kneel;
                break;
            case SayLexlort3:
                if (_nuzark is not null)
                {
                    System?.PlayEmote(_nuzark, EmoteOneshotAttack2hTight);
                }

                break;
            case 0:
                // The fake death when the axe comes down.
                System?.InterruptCast(Me);
                Me.UnitFlags |= UnitFlags.NotSelectable;
                Me.Motion.Clear();
                Me.StandState = StandState.Dead;
                break;
            case SayLexlort4:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestPrecariousPredicament, Me);
                }

                Me.Map?.Combat.Kill(Me, Me);
                break;
        }

        _outroMs = delay;
        if (delay == 0)
        {
            _outroStep = -1;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_outroStep >= 0)
        {
            if (_outroMs <= diffMs)
            {
                _outroStep++;
                DoOutroStep();
            }
            else
            {
                _outroMs -= diffMs;
            }
        }

        UpdateVictim();
    }
}

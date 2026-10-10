using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// "In Dreams" (quest 5944, Western Plaguelands): mangos-classic ScriptDev2 <c>npc_taelan_fordring</c>, <c>npc_isillien</c> and
/// <c>npc_tirion_fordring</c> (eastern_kingdoms/western_plaguelands.cpp at 3e8597afe7) on the z2815 paths (57, 3 and 4 points).
/// Taelan walks out of Scarlet keep, mounts and rides to the tower, where Grand Inquisitor Isillien and his elites come out. Isillien
/// strikes Taelan down when he drops below half health and turns on the player; two minutes later (or when Isillien drops below 20%)
/// Tirion rides in, kneels by his son, kills Isillien and then holds Taelan for the epilogue, which credits the quest.
/// The Scarlet Subterfuge (5862) reward scene is not ported: ArcaneCore has no quest-rewarded hook for creature scripts.
/// </summary>
public sealed class TaelanFordringAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 1842, QuestInDreams = 5944, FactionEscortNeutralFriendPassive = 290, ModelTaelanMount = 239;
    public const uint NpcIsillien = IsillienAI.Entry, NpcTirion = TirionFordringAI.Entry, NpcCrimsonElite = 12128;
    public const uint SpellDevotionAura = 17232, SpellCrusaderStrike = 14518, SpellHolyStrike = 17143, SpellHolyCleave = 18819,
        SpellHolyLight = 15493, SpellLayOnHands = 17233;
    public const int SayEscortStart = -1001078, SayKillTaelan1 = -1001090, SayEpilog1 = -1001099;
    private const int TagTirionKneels = 1;

    private enum Speaker { None, Taelan, Isillien, Tirion }

    private static readonly (int Entry, Speaker Speaker, uint Delay)[] s_dialogue =
    [
        (-1001079, Speaker.Taelan, 6000), (-1001080, Speaker.Taelan, 4000), ((int)ModelTaelanMount, Speaker.None, 0),
        (-1001081, Speaker.Taelan, 2000), (-1001082, Speaker.Isillien, 5000), (-1001083, Speaker.Taelan, 4000),
        (-1001084, Speaker.Taelan, 10000), (-1001085, Speaker.Isillien, 7000), (-1001086, Speaker.Isillien, 5000),
        (-1001087, Speaker.Isillien, 6000), (-1001088, Speaker.Isillien, 3000), (-1001089, Speaker.Isillien, 3000),
        ((int)SpellCrusaderStrike, Speaker.None, 0),
        (SayKillTaelan1, Speaker.Isillien, 1000), (-1001091, Speaker.Isillien, 4000), (-1001092, Speaker.Isillien, 10000),
        (-1001093, Speaker.Isillien, 0),
        ((int)NpcTirion, Speaker.None, 5000), ((int)NpcIsillien, Speaker.None, 10000), (-1001094, Speaker.Tirion, 4000),
        (-1001095, Speaker.Isillien, 6000), (-1001096, Speaker.Tirion, 4000), (-1001097, Speaker.Tirion, 3000),
        (-1001098, Speaker.Isillien, 0),
        (SayEpilog1, Speaker.Tirion, 3000), (TagTirionKneels, Speaker.None, 3000), (-1001100, Speaker.Tirion, 4000),
        (-1001101, Speaker.Tirion, 5000), (-1001102, Speaker.Tirion, 5000), (-1001103, Speaker.Tirion, 6000),
        (-1001104, Speaker.Tirion, 7000), (-1001105, Speaker.Tirion, 0),
    ];

    private bool _fightStarted, _hasMount, _taelanDead;
    private Creature? _isillien, _tirion;
    private uint _holyCleaveMs, _holyStrikeMs, _crusaderStrikeMs, _holyLightMs, _stepMs;
    private int _step = -1;

    public bool TaelanDead => _taelanDead;
    public Creature? Isillien => _isillien;
    public Creature? Tirion => _tirion;
    public int DialogueStep => _step;

    protected override bool HoldAtEndWhilePaused => true;

    protected override void Reset()
    {
        _holyCleaveMs = Rand(11000, 15000);
        _holyStrikeMs = Rand(6000, 8000);
        _crusaderStrikeMs = Rand(1000, 5000);
        _holyLightMs = 0;
    }

    private uint Rand(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestInDreams)
        {
            return;
        }

        Start(run: false, player: player, questId: questId);
        System?.SayText(Me, SayEscortStart);
        Me.FactionTemplate = FactionEscortNeutralFriendPassive;
    }

    protected override void Aggro(Unit target)
    {
        if (_hasMount)
        {
            Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
        }

        DoCast(Me, SpellDevotionAura);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (!_taelanDead)
        {
            base.MoveInLineOfSight(who);
        }
    }

    /// <summary>EnterEvadeMode while "dead": he lies where he fell; otherwise he mounts again and the escort evades.</summary>
    public override bool OnEnterEvadeMode()
    {
        if (!_taelanDead)
        {
            if (_hasMount)
            {
                Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, ModelTaelanMount);
            }

            return false;
        }

        if (System is { } system)
        {
            system.StopCombatInPlace(Me);
            system.SetAiImmobilized(Me, true, combatOnly: false);
        }

        Me.Health = 1;
        Me.UnitFlags |= UnitFlags.NotSelectable;
        Me.StandState = StandState.Dead;
        Reset();
        return true;
    }

    /// <summary>EffectDummyCreature_npc_taelan_fordring (SPELL_TAELAN_DEATH from Isillien): Taelan and Isillien both evade.</summary>
    internal void StruckDown()
    {
        _taelanDead = true;
        System?.EnterEvadeMode(Me);
    }

    /// <summary>AI_EVENT_CUSTOM_B from Isillien: Tirion is summoned and rides in.</summary>
    internal void SummonTirion()
    {
        if (System is not { } system)
        {
            return;
        }

        StartDialogue(Index((int)NpcTirion));
        // TEMPSPAWN_TIMED_OOC_OR_DEAD_DESPAWN, 15 min
        _tirion = system.SummonAt(Me, NpcTirion, 2620.273f, -1920.917f, 74.25f, 0f, null, 15 * 60_000);
        if (_tirion is not null)
        {
            _tirion.UnitFlags |= UnitFlags.ImmuneToNpc; // until he attacks Isillien, so the nearby Scarlets leave the scene alone
            (_tirion.AI as TirionFordringAI)?.StartFrom(this);
        }
    }

    /// <summary>SummonedMovementInform for Tirion's custom points: 100 at Taelan's side, 200 back after Isillien is dead.</summary>
    internal void TirionArrived(uint pointId)
    {
        if (_tirion is null)
        {
            return;
        }

        if (pointId == 100)
        {
            StartDialogue(Index((int)NpcIsillien));
            Face(_tirion, Me);
            _tirion.StandState = StandState.Kneel;
        }
        else if (pointId == 200)
        {
            StartDialogue(Index(SayEpilog1));
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 26:
                SetEscortPaused(true);
                StartDialogue(0);
                break;
            case 56:
                SetEscortPaused(true);
                StartDialogue(Index(-1001081));
                break;
        }
    }

    private static int Index(int entry) => Array.FindIndex(s_dialogue, d => d.Entry == entry);

    private void Face(Creature who, WorldObject at) => System?.SetFacingTo(who, MathF.Atan2(at.Y - who.Y, at.X - who.X));

    private void StartDialogue(int index)
    {
        _step = index;
        _stepMs = s_dialogue[index].Delay;
        DoStep(s_dialogue[index]);
    }

    private void DoStep((int Entry, Speaker Speaker, uint Delay) step)
    {
        if (System is not { } system)
        {
            return;
        }

        Creature? speaker = step.Speaker switch
        {
            Speaker.Taelan => Me,
            Speaker.Isillien => _isillien,
            Speaker.Tirion => _tirion,
            _ => null,
        };
        if (speaker is not null)
        {
            system.SayText(speaker, step.Entry);
        }

        switch (step.Entry)
        {
            case (int)ModelTaelanMount: // mount when outside
                _hasMount = true;
                SetEscortPaused(false);
                Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, ModelTaelanMount);
                break;
            case -1001081: // SAY_REACH_TOWER: Isillien and two elites come out
                if (GetPlayerForEscort() is { } player)
                {
                    Face(Me, player);
                }

                _isillien = system.SummonAt(Me, NpcIsillien, 2693.12f, -1943.04f, 72.04f, 2.11f, null, 15 * 60_000);
                if (_isillien is not null)
                {
                    (_isillien.AI as IsillienAI)?.StartFrom(this);
                    float o = _isillien.Orientation;
                    foreach (float angle in new[] { MathF.PI * 1.25f, 0f })
                    {
                        system.SummonAt(_isillien, NpcCrimsonElite, _isillien.X + (5f * MathF.Cos(o + angle)), _isillien.Y + (5f * MathF.Sin(o + angle)),
                            _isillien.Z, o, null, 15 * 60_000);
                    }
                }

                break;
            case -1001083: // SAY_ISILLIEN_2
                if (_isillien is not null)
                {
                    Face(Me, _isillien);
                }

                break;
            case -1001084: // SAY_ISILLIEN_3
                Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
                break;
            case (int)SpellCrusaderStrike: // three more elites run in; Isillien and Taelan fight
                foreach ((float x, float y, float z) in new[] { (2711.32f, -1882.67f, 67.89f), (2710.93f, -1878.90f, 67.97f), (2710.53f, -1875.28f, 67.90f) })
                {
                    if (system.SummonAt(Me, NpcCrimsonElite, x, y, z, 3.2f, null, 15 * 60_000) is { } elite)
                    {
                        float a = MathF.Atan2(elite.Y - Me.Y, elite.X - Me.X);
                        system.MoveTo(elite, Me.X + (2f * MathF.Cos(a)), Me.Y + (2f * MathF.Sin(a)), Me.Z, run: true, finalOrientation: null);
                    }
                }

                if (_isillien is not null)
                {
                    system.AttackStart(_isillien, Me);
                    system.AttackStart(Me, _isillien);
                }

                _fightStarted = true;
                break;
            case SayKillTaelan1:
                (_isillien?.AI as IsillienAI)?.KillTaelan(Me);
                break;
            case -1001093: // EMOTE_ATTACK_PLAYER
                if (_isillien is not null && GetPlayerForEscort() is { } target)
                {
                    system.AttackStart(_isillien, target);
                }

                break;
            case -1001094: // SAY_TIRION_1
                if (_tirion is not null)
                {
                    _tirion.StandState = StandState.Stand;
                    if (_isillien is not null)
                    {
                        Face(_tirion, _isillien);
                        system.StopCombatInPlace(_isillien);
                    }
                }

                break;
            case -1001098: // SAY_TIRION_5
                if (_isillien is not null && _tirion is not null)
                {
                    _tirion.UnitFlags &= ~UnitFlags.ImmuneToNpc;
                    system.AttackStart(_tirion, _isillien);
                    system.AttackStart(_isillien, _tirion);
                }

                break;
            case SayEpilog1:
                if (_isillien is not null && _tirion is not null)
                {
                    Face(_tirion, _isillien);
                }

                break;
            case TagTirionKneels:
                if (_tirion is not null)
                {
                    Face(_tirion, Me);
                }

                break;
            case -1001102: // EMOTE_HOLD_TAELAN
                if (_tirion is not null)
                {
                    _tirion.StandState = StandState.Kneel;
                }

                break;
            case -1001104: // SAY_EPILOG_4
                if (_tirion is not null)
                {
                    _tirion.StandState = StandState.Stand;
                }

                break;
            case -1001105: // SAY_EPILOG_5
                if (_tirion is not null)
                {
                    if (GetPlayerForEscort() is { } rewarded)
                    {
                        system.RewardGroupEventExplored(rewarded, QuestInDreams, Me);
                    }

                    system.ForcedDespawn(_tirion, 3 * 60_000);
                    _tirion.NpcFlags |= (uint)NpcFlags.QuestGiver;
                }

                system.ForcedDespawn(Me, 3 * 60_000);
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_step >= 0)
        {
            if (_stepMs == 0)
            {
                _step = -1;
            }
            else if (_stepMs <= diffMs)
            {
                if (_step + 1 < s_dialogue.Length)
                {
                    StartDialogue(_step + 1);
                }
                else
                {
                    _step = -1;
                }
            }
            else
            {
                _stepMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim || _taelanDead)
        {
            return;
        }

        // In the fight with Isillien he "dies" below half health.
        if (_fightStarted && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 50)
        {
            StartDialogue(Index(SayKillTaelan1));
            _taelanDead = true;
        }

        Tick(ref _holyCleaveMs, diffMs, () => DoCast(victim, SpellHolyCleave), 11000, 13000);
        Tick(ref _holyStrikeMs, diffMs, () => DoCast(victim, SpellHolyStrike), 9000, 14000);
        Tick(ref _crusaderStrikeMs, diffMs, () => DoCast(victim, SpellCrusaderStrike), 7000, 12000);
        if ((ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 75)
        {
            if (_holyLightMs < diffMs)
            {
                if (SelectLowestHpFriendly(50f) is { } friend && DoCast(friend, SpellHolyLight) == CreatureCastResult.Ok)
                {
                    _holyLightMs = Rand(10000, 15000);
                }
            }
            else
            {
                _holyLightMs -= diffMs;
            }
        }

        if (!_fightStarted && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 15)
        {
            DoCast(Me, SpellLayOnHands);
        }
    }

    internal void Tick(ref uint timer, uint diffMs, Func<CreatureCastResult> cast, int min, int max)
    {
        if (timer < diffMs)
        {
            if (cast() == CreatureCastResult.Ok)
            {
                timer = Rand(min, max);
            }
        }
        else
        {
            timer -= diffMs;
        }
    }
}

/// <summary><c>npc_isillien</c>: walks out of the tower (3 points), fights only on request, and brings Tirion when it is time.</summary>
public sealed class IsillienAI(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 1840;
    public const uint SpellDominateMind = 14515, SpellFlashHeal = 10917, SpellGreaterHeal = 10965, SpellManaBurn = 15800, SpellMindBlast = 17194,
        SpellMindFlay = 17165, SpellTaelanDeath = 18969;

    private TaelanFordringAI? _taelan;

    /// <summary>He pauses at his last point (3) outside the tower and stays for the scene.</summary>
    protected override bool HoldAtEndWhilePaused => true;
    private bool _tirionSpawned, _taelanDead;
    private uint _summonTirionMs, _manaBurnMs, _flashHealMs, _greaterHealMs, _dominateMs, _mindBlastMs, _mindFlayMs;

    protected override void Reset()
    {
        _manaBurnMs = Rand(7000, 12000);
        _flashHealMs = Rand(10000, 15000);
        _dominateMs = Rand(10000, 14000);
        _mindBlastMs = Rand(0, 1000);
        _mindFlayMs = Rand(3000, 7000);
        _greaterHealMs = 0;
    }

    private uint Rand(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    /// <summary>Attacks only on request.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
    }

    public override bool OnEnterEvadeMode()
    {
        if (!_taelanDead)
        {
            return false;
        }

        System?.StopCombatInPlace(Me);
        Reset();
        return true;
    }

    internal void StartFrom(TaelanFordringAI taelan)
    {
        _taelan = taelan;
        Start(run: false);
    }

    /// <summary>AI_EVENT_CUSTOM_A: SPELL_TAELAN_DEATH; Tirion comes two minutes later.</summary>
    internal void KillTaelan(Creature taelan)
    {
        DoCast(taelan, SpellTaelanDeath, triggered: true);
        _taelanDead = true;
        _summonTirionMs = 120_000;
        System?.SetFacingTo(Me, MathF.Atan2(taelan.Y - Me.Y, taelan.X - Me.X));
        (taelan.AI as TaelanFordringAI)?.StruckDown();
        System?.EnterEvadeMode(Me);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId == 3)
        {
            SetEscortPaused(true);
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (!_tirionSpawned && ((_summonTirionMs != 0 && _summonTirionMs <= diffMs) || (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 20))
        {
            _taelan?.SummonTirion();
            _tirionSpawned = true;
        }
        else if (_summonTirionMs != 0)
        {
            _summonTirionMs -= diffMs; // never below diffMs here: a smaller timer fired above
        }

        if (!UpdateVictim() || Victim is not { } victim || _taelan is null)
        {
            return;
        }

        _taelan.Tick(ref _mindBlastMs, diffMs, () => DoCast(victim, SpellMindBlast), 3000, 5000);
        _taelan.Tick(ref _mindFlayMs, diffMs, () => DoCast(victim, SpellMindFlay), 9000, 15000);
        _taelan.Tick(ref _manaBurnMs, diffMs, () => DoCast(victim, SpellManaBurn), 8000, 12000);
        _taelan.Tick(ref _dominateMs, diffMs, () => victim is Player ? DoCast(victim, SpellDominateMind) : CreatureCastResult.Failed, 25000, 30000);
        _taelan.Tick(ref _flashHealMs, diffMs, () => SelectLowestHpFriendly(50f) is { } friend ? DoCast(friend, SpellFlashHeal) : CreatureCastResult.Failed,
            10000, 15000);
        if ((ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 50)
        {
            _taelan.Tick(ref _greaterHealMs, diffMs, () => DoCast(Me, SpellGreaterHeal), 15000, 20000);
        }
    }
}

/// <summary><c>npc_tirion_fordring</c>: rides in (4 points), kneels by Taelan, kills Isillien, then goes back to Taelan.</summary>
public sealed class TirionFordringAI(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 12126;

    private TaelanFordringAI? _taelan;
    private uint _holyCleaveMs, _holyStrikeMs, _crusaderStrikeMs, _customPoint;

    protected override bool HoldAtEndWhilePaused => true;
    private float _targetX, _targetY;

    protected override void Reset()
    {
        _holyCleaveMs = Rand(11000, 15000);
        _holyStrikeMs = Rand(6000, 8000);
        _crusaderStrikeMs = Rand(1000, 5000);
    }

    private uint Rand(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    protected override void Aggro(Unit target) => DoCast(Me, TaelanFordringAI.SpellDevotionAura);

    public override void MoveInLineOfSight(Unit who)
    {
    }

    /// <summary>On evade he goes back to Taelan (point 200).</summary>
    public override bool OnEnterEvadeMode()
    {
        System?.StopCombatInPlace(Me);
        GoToTaelan(200);
        Reset();
        return true;
    }

    internal void StartFrom(TaelanFordringAI taelan)
    {
        _taelan = taelan;
        Start(run: true);
    }

    private void GoToTaelan(uint pointId)
    {
        if (_taelan?.Me is not { } taelan || System is not { } system)
        {
            return;
        }

        float a = MathF.Atan2(Me.Y - taelan.Y, Me.X - taelan.X);
        _targetX = taelan.X + (2f * MathF.Cos(a));
        _targetY = taelan.Y + (2f * MathF.Sin(a));
        _customPoint = pointId;
        system.MoveTo(Me, _targetX, _targetY, taelan.Z, run: false, finalOrientation: null);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId == 3)
        {
            SetEscortPaused(true);
            Me.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
            GoToTaelan(100);
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_customPoint != 0 && ((Me.X - _targetX) * (Me.X - _targetX)) + ((Me.Y - _targetY) * (Me.Y - _targetY)) <= 1f)
        {
            uint point = _customPoint;
            _customPoint = 0;
            _taelan?.TirionArrived(point);
        }

        if (!UpdateVictim() || Victim is not { } victim || _taelan is null)
        {
            return;
        }

        _taelan.Tick(ref _holyCleaveMs, diffMs, () => DoCast(victim, TaelanFordringAI.SpellHolyCleave), 12000, 15000);
        _taelan.Tick(ref _holyStrikeMs, diffMs, () => DoCast(victim, TaelanFordringAI.SpellHolyStrike), 8000, 11000);
        _taelan.Tick(ref _crusaderStrikeMs, diffMs, () => DoCast(victim, TaelanFordringAI.SpellCrusaderStrike), 7000, 9000);
    }
}

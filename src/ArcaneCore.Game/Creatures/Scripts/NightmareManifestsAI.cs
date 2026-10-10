using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Keeper Remulos (entry 11832, Moonglade), "Nightmare Manifests" (quest 8736): mangos-classic ScriptDev2 <c>npc_keeper_remulos</c>
/// (kalimdor/moonglade.cpp at 3e8597afe7) on the z2815 path (19 points). He walks to the lake, conjures the Dream Rift (Eranikus, Tyrant
/// of the Dream, hovers above it), talks with him, then walks into Nighthaven and holds the village: ten waves of Nightmare Phantasms,
/// with Nighthaven defenders summoned to help, after which Eranikus lands and fights (<see cref="EranikusAI"/>). The redeemed Eranikus
/// hands him the outro and the quest credit. The spell script <c>spell_conjure_dream_rift</c> is folded in here (it only summons
/// Eranikus); the defenders stand where they spawn (the source sends them along a creature_movement path ArcaneCore's summons lack).
/// </summary>
public sealed class KeeperRemulosAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 11832, QuestNightmareManifests = 8736, FactionCenarionCircle = 996;
    public const uint NpcEranikusTyrant = EranikusAI.Entry, NpcNightmarePhantasm = 15629, NpcNighthavenDefender = 15495;
    public const uint SpellConjureRift = 25813, SpellDragonHover = 18430, SpellHealingTouch = 23381, SpellRegrowth = 20665,
        SpellRejuvenation = 20664, SpellStarfire = 21668;
    public const int SayIntro1 = -1000669, SayIntro2 = -1000670, SayIntro3 = -1000671, EmoteSummonEranikus = -1000674, SayDefend1 = -1000682,
        SayEranikusAttack1 = -1000686, SayOutro1 = -1000704, SayOutro2 = -1000705;
    public const int MaxShadows = 3, MaxDefenders = 10, MaxSummonTurns = 10;

    internal static readonly (float X, float Y, float Z, float O)[] EranikusLocations =
    [
        (7881.72f, -2651.23f, 493.29f, 0.40f), (7929.86f, -2574.88f, 505.35f, 0f), (7912.98f, -2568.99f, 488.71f, 0f), (7906.57f, -2565.63f, 488.39f, 0f),
    ];

    private static readonly (float X, float Y, float Z)[] s_shadows =
    [
        (7832.78f, -2604.57f, 489.29f), (7826.68f, -2538.46f, 489.30f), (7811.48f, -2573.20f, 488.49f),
        (7888.32f, -2566.25f, 487.02f), (7946.12f, -2577.10f, 489.97f), (7963.00f, -2492.03f, 487.84f),
    ];

    private static readonly (float X, float Y, float Z)[] s_defenders = [(7868.226f, -2556.95f, 487.07f), (7867.39f, -2578.96f, 486.95f)];

    private enum Speaker { None, Remulos, Eranikus }

    private static readonly (int Entry, Speaker Speaker, uint Delay)[] s_dialogue =
    [
        ((int)Entry, Speaker.None, 14000), (-1000672, Speaker.Remulos, 12000), (-1000673, Speaker.Remulos, 5000),
        ((int)SpellConjureRift, Speaker.None, 13000), (-1000675, Speaker.Eranikus, 11000), (-1000676, Speaker.Remulos, 5000),
        (-1000677, Speaker.Eranikus, 3000), (-1000678, Speaker.Eranikus, 10000), (-1000679, Speaker.Remulos, 12000),
        (-1000680, Speaker.Eranikus, 6000), (-1000681, Speaker.Eranikus, 7000), ((int)NpcEranikusTyrant, Speaker.None, 0),
        (-1000683, Speaker.Remulos, 6000), (-1000684, Speaker.Eranikus, 4000), (-1000685, Speaker.Remulos, 0),
    ];

    private uint _healMs, _starfireMs, _shadeSummonMs, _outroMs, _stepMs;
    private int _outroPhase, _summonCount, _step = -1;
    private bool _firstWave;
    private Creature? _eranikus;
    private readonly List<Creature> _defenders = [];

    public Creature? Eranikus => _eranikus;
    public int SummonTurns => _summonCount;
    public int DialogueStep => _step;

    protected override bool HoldAtEndWhilePaused => true;

    protected override void Reset()
    {
        if (HasEscortState(EscortState.Escorting))
        {
            return;
        }

        _outroMs = 0;
        _outroPhase = 0;
        _summonCount = 0;
        _eranikus = null;
        _shadeSummonMs = 0;
        _healMs = 10_000;
        _starfireMs = 25_000;
        _firstWave = true;
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestNightmareManifests)
        {
            Start(run: true, player: player, questId: questId);
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (summoned.Entry == NpcNighthavenDefender)
        {
            _defenders.Remove(summoned);
        }

        base.OnSummonedCreatureJustDied(summoned);
    }

    public override void OnDeath(Unit? killer)
    {
        // Eranikus evades so his summons go; Remulos was only targetable by friendly spells during the event.
        if (_eranikus is { IsAlive: true } eranikus)
        {
            System?.EnterEvadeMode(eranikus);
        }

        Me.UnitFlags &= ~UnitFlags.Pvp;
        base.OnDeath(killer);
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.SayText(Me, SayIntro1, player);
                    Me.UnitFlags |= UnitFlags.Pvp;
                }

                break;
            case 2:
                System?.SayText(Me, SayIntro2);
                break;
            case 14:
                StartDialogue(0);
                SetEscortPaused(true);
                break;
            case 18:
                StartDialogue(Index(-1000683));
                SetEscortPaused(true);
                break;
            case 19:
                SetEscortPaused(true);
                break;
        }
    }

    private static int Index(int entry) => Array.FindIndex(s_dialogue, d => d.Entry == entry);

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

        Creature? speaker = step.Speaker switch { Speaker.Remulos => Me, Speaker.Eranikus => _eranikus, _ => null };
        if (speaker is not null)
        {
            system.SayText(speaker, step.Entry);
        }

        switch (step.Entry)
        {
            case (int)Entry:
                if (GetPlayerForEscort() is { } player)
                {
                    system.SayText(Me, SayIntro3, player);
                }

                break;
            case (int)SpellConjureRift: // the rift spell and its script: Eranikus hovers above the lake, out of reach for now
                DoCast(Me, SpellConjureRift, triggered: true);
                (float x, float y, float z, float o) = EranikusLocations[0];
                _eranikus = system.SummonCorpseTimedDespawn(Me, NpcEranikusTyrant, x, y, z, o, null, 0);
                if (_eranikus is not null)
                {
                    _eranikus.UnitFlags |= UnitFlags.ImmuneToNpc | UnitFlags.ImmuneToPlayer;
                    system.CastSpell(_eranikus, SpellDragonHover, _eranikus, true);
                }

                break;
            case -1000675: // SAY_ERANIKUS_SPAWN
                if (_eranikus is not null)
                {
                    system.SayText(_eranikus, EmoteSummonEranikus);
                }

                break;
            case (int)NpcEranikusTyrant: // Eranikus flies above the village; on to Nighthaven
                if (GetPlayerForEscort() is { } leader)
                {
                    system.SayText(Me, SayDefend1, leader);
                }

                if (_eranikus is not null)
                {
                    system.RemoveAuras(_eranikus, SpellDragonHover);
                    (_eranikus.AI as EranikusAI)?.MoveToPoint(EranikusAI.PointFlight, EranikusLocations[1], this);
                }

                SetEscortPaused(false);
                break;
            case -1000683: // SAY_REMULOS_DEFEND_2
                if (_eranikus is not null)
                {
                    system.SetFacingTo(Me, MathF.Atan2(_eranikus.Y - Me.Y, _eranikus.X - Me.X));
                }

                break;
            case -1000685: // SAY_REMULOS_DEFEND_3: the shades come
                SetEscortPaused(true);
                Me.FactionTemplate = FactionCenarionCircle;
                _shadeSummonMs = 5_000;
                break;
        }
    }

    /// <summary>DoHandleOutro, from the redeemed Eranikus: the credit, the defenders go, and his closing lines.</summary>
    internal void HandleOutro(Creature target)
    {
        if (GetPlayerForEscort() is { } player)
        {
            System?.RewardGroupEventExplored(player, QuestNightmareManifests, target);
        }

        foreach (Creature defender in _defenders.ToArray())
        {
            System?.ForcedDespawn(defender, 0);
        }

        _defenders.Clear();
        Me.UnitFlags &= ~UnitFlags.Pvp;
        _outroMs = 3_000;
    }

    private (float X, float Y, float Z) RandomPoint((float X, float Y, float Z) at, float radius)
    {
        double angle = (System?.RandomInt(0, 3600) ?? 0) / 3600.0 * Math.Tau;
        float distance = radius * (System?.RandomInt(0, 1000) ?? 0) / 1000f;
        return (at.X + (distance * (float)Math.Cos(angle)), at.Y + (distance * (float)Math.Sin(angle)), at.Z);
    }

    private void SummonDefender(int index, bool timed)
    {
        (float x, float y, float z) = RandomPoint(s_defenders[index % 2], 3f);
        Creature? defender = timed
            ? System?.SummonAt(Me, NpcNighthavenDefender, x, y, z, 0f, null, 3 * 60_000, oocOrCorpse: false)
            : System?.SummonCorpseTimedDespawn(Me, NpcNighthavenDefender, x, y, z, 0f, null, 0);
        if (defender is not null)
        {
            _defenders.Add(defender);
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

        if (_outroMs != 0)
        {
            if (_outroMs <= diffMs)
            {
                if (_outroPhase == 0)
                {
                    System?.SayText(Me, SayOutro1);
                    _outroMs = 3_000;
                }
                else
                {
                    // He despawns and comes back home on his own a minute later.
                    System?.SayText(Me, SayOutro2);
                    Me.RespawnDelayOverrideSeconds = 60;
                    System?.ForcedDespawn(Me, 3_000);
                    _outroMs = 0;
                }

                ++_outroPhase;
            }
            else
            {
                _outroMs -= diffMs;
            }
        }

        if (_shadeSummonMs != 0 && System is { } system)
        {
            if (_shadeSummonMs <= diffMs)
            {
                if (_firstWave)
                {
                    // TEMPSPAWN_DEAD_DESPAWN shades inside the houses, and the defenders
                    for (int i = 0; i < MaxShadows; i++)
                    {
                        SummonShade(s_shadows[i], timed: false);
                    }

                    for (int i = 0; i < MaxDefenders; i++)
                    {
                        SummonDefender(i, timed: true);
                    }

                    if (_eranikus is not null)
                    {
                        system.SayText(_eranikus, SayEranikusAttack1);
                    }

                    ++_summonCount;
                    _firstWave = false;
                }

                int summonPoint = system.RandomInt(0, 99) < 70 ? MaxShadows : system.RandomInt(MaxShadows + 1, MaxShadows + 2);
                if (_summonCount < MaxSummonTurns)
                {
                    for (int i = 0; i < MaxShadows; i++)
                    {
                        SummonShade(RandomPoint(s_shadows[summonPoint], 10f), timed: true);
                    }

                    for (int i = 0, missing = MaxDefenders - _defenders.Count; i < missing; i++)
                    {
                        SummonDefender(i, timed: false);
                    }

                    ++_summonCount;
                }

                // Every wave is out: Eranikus lands and fights (dead shades are not waited for).
                if (_summonCount == MaxSummonTurns)
                {
                    _shadeSummonMs = 0;
                    if (_eranikus is not null)
                    {
                        (_eranikus.AI as EranikusAI)?.MoveToPoint(EranikusAI.PointCombat, EranikusLocations[2], this);
                    }
                }
                else
                {
                    _shadeSummonMs = (uint)system.RandomInt(20_000, 30_000);
                }
            }
            else
            {
                _shadeSummonMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_healMs < diffMs)
        {
            if (SelectLowestHpFriendly(100f) is { } friend)
            {
                DoCast(friend, (System?.RandomInt(0, 2) ?? 0) switch { 0 => SpellHealingTouch, 1 => SpellRejuvenation, _ => SpellRegrowth });
            }

            _healMs = 10_000;
        }
        else
        {
            _healMs -= diffMs;
        }

        if (_starfireMs < diffMs)
        {
            if (DoCast(victim, SpellStarfire) == CreatureCastResult.Ok)
            {
                _starfireMs = 20_000;
            }
        }
        else
        {
            _starfireMs -= diffMs;
        }
    }

    private void SummonShade((float X, float Y, float Z) at, bool timed)
    {
        Creature? shade = timed
            ? System?.SummonAt(Me, NpcNightmarePhantasm, at.X, at.Y, at.Z, 0f, null, 3 * 60_000, oocOrCorpse: false)
            : System?.SummonCorpseTimedDespawn(Me, NpcNightmarePhantasm, at.X, at.Y, at.Z, 0f, null, 0);
        if (shade is not null)
        {
            shade.FactionTemplate = 14; // "ToDo: set faction to DB"
            System?.AttackStart(shade, Me);
        }
    }
}

/// <summary>
/// Eranikus, Tyrant of the Dream (entry 15491): mangos-classic <c>boss_eranikusAI</c> (moonglade.cpp at 3e8597afe7). Lands at the end of
/// the shade waves and fights without moving; Tyrande calls out at 85%, joins at 50%, her priestesses at 35%; below 20% he evades
/// redeemed (entry 15628), and after the redemption scene hands Remulos the outro and the credit. Any other evade ends the event.
/// </summary>
public sealed class EranikusAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint Entry = 15491, NpcEranikusRedeemed = 15628, NpcTyrande = 15633, NpcElunePriestess = 15634, PriestessMount = 9695;
    public const uint SpellAcidBreath = 24839, SpellNoxiousBreath = 24818, SpellShadowboltVolley = 25586, SpellArcaneChanneling = 23017,
        SpellEranikusRedeemed = 25846;
    public const uint PointFlight = 1, PointCombat = 2, PointRedeemed = 3, EmoteOneshotLand = 293, EmoteOneshotBow = 2;
    public const int SayAttack2 = -1000687, SayAttack3 = -1000688, SayKill = -1000706, SayTyrandeAppear = -1000689, SayTyrandeHeal = -1000690,
        SayTyrandeForgiven1 = -1000691, SayTyrandeForgiven2 = -1000692, SayTyrandeForgiven3 = -1000693, SayDefeat1 = -1000694,
        SayDefeat2 = -1000695, SayDefeat3 = -1000696, EmoteRedeem = -1000697, EmoteTyrandeKneel = -1000698, SayTyrandeRedeemed = -1000699,
        SayRedeemed1 = -1000700, SayRedeemed2 = -1000701, SayRedeemed3 = -1000702, SayRedeemed4 = -1000703;
    private const int MaxPriestess = 7;

    private static readonly (float X, float Y, float Z, float O)[] s_tyrande =
    [
        (7948.89f, -2575.58f, 490.05f, 3.03f), (7888.32f, -2566.25f, 487.02f, 0f), (7901.83f, -2565.24f, 488.04f, 0f),
    ];

    private uint _acidBreathMs, _noxiousBreathMs, _volleyMs, _eventMs, _tyrandeMoveMs;
    private int _eventPhase, _healthCheck;
    private Creature? _tyrande;
    private KeeperRemulosAI? _remulos;
    private readonly List<Creature> _priestesses = [];
    private readonly List<(Creature Who, uint Point, float X, float Y)> _moves = [];

    public bool Redeemed => Me.Entry == NpcEranikusRedeemed;
    public int HealthCheck => _healthCheck;

    public override void OnRespawn()
    {
        _acidBreathMs = 10_000;
        _noxiousBreathMs = 3_000;
        _volleyMs = 5_000;
        _tyrandeMoveMs = 0;
        _tyrande = null;
        _healthCheck = 85;
        _eventPhase = 0;
        _eventMs = 0;
        CombatMovement = false; // "for some reason the boss doesn't move in combat"
    }

    /// <summary>He attacks only once he lands (no aggro while he hovers out of reach).</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if ((Me.UnitFlags & UnitFlags.ImmuneToPlayer) == 0 && !Redeemed)
        {
            base.MoveInLineOfSight(who);
        }
    }

    public override void OnKilledUnit(Unit victim)
    {
        if (victim is Player)
        {
            System?.SayText(Me, SayKill);
        }
    }

    internal void MoveToPoint(uint pointId, (float X, float Y, float Z, float O) at, KeeperRemulosAI? remulos = null)
    {
        _remulos ??= remulos;
        MoveTracked(Me, pointId, at.X, at.Y, at.Z, run: true);
    }

    private void MoveTracked(Creature who, uint pointId, float x, float y, float z, bool run)
    {
        _moves.RemoveAll(m => ReferenceEquals(m.Who, who));
        _moves.Add((who, pointId, x, y));
        System?.MoveTo(who, x, y, z, run, finalOrientation: null);
    }

    public override bool OnEnterEvadeMode()
    {
        if (System is not { } system)
        {
            return false;
        }

        if ((ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 20)
        {
            system.StopCombatInPlace(Me);
            if (_remulos?.Me is { } remulos)
            {
                system.EnterEvadeMode(remulos);
            }

            DespawnPriestesses();
            _eventMs = 5_000;
            system.UpdateEntry(Me, NpcEranikusRedeemed);
            return true;
        }

        // Any other evade ends the event: he, his priestesses and Tyrande go.
        system.ForcedDespawn(Me, 0);
        DespawnPriestesses();
        if (_tyrande is not null)
        {
            system.ForcedDespawn(_tyrande, 0);
        }

        return true;
    }

    private void DespawnPriestesses()
    {
        foreach (Creature priestess in _priestesses)
        {
            System?.ForcedDespawn(priestess, 0);
        }

        _priestesses.Clear();
    }

    private void Arrived(Creature who, uint pointId)
    {
        if (System is not { } system)
        {
            return;
        }

        if (ReferenceEquals(who, Me))
        {
            switch (pointId)
            {
                case PointFlight when _remulos?.Me is { } remulos:
                    system.SetFacingTo(Me, MathF.Atan2(remulos.Y - Me.Y, remulos.X - Me.X));
                    break;
                case PointCombat: // land and fight
                    Me.UnitFlags &= ~(UnitFlags.ImmuneToNpc | UnitFlags.ImmuneToPlayer);
                    if (_remulos?.Me is { IsAlive: true } target)
                    {
                        system.AttackStart(Me, target);
                    }

                    system.SayText(Me, SayAttack2);
                    system.RemoveAuras(Me, KeeperRemulosAI.SpellDragonHover);
                    system.PlayEmote(Me, EmoteOneshotLand);
                    break;
                case PointRedeemed:
                    system.SayText(Me, SayRedeemed1);
                    _eventMs = 11_000;
                    break;
            }

            return;
        }

        if (pointId == 0) // POINT_ID_TYRANDE_HEAL
        {
            who.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
            if (who.Entry == NpcTyrande)
            {
                system.SayText(who, SayTyrandeHeal);
                _tyrandeMoveMs = 5_000;
            }
            else
            {
                system.AttackStart(who, Me); // they need to be in combat to heal
            }
        }
        else if (pointId == 1 && who.Entry == NpcTyrande) // POINT_ID_TYRANDE_ABSOLUTION
        {
            system.CastSpell(who, SpellArcaneChanneling, who, false);
            system.SayText(who, SayTyrandeForgiven1);
        }
    }

    private (float X, float Y, float Z) RandomPoint(float x, float y, float z, float radius)
    {
        double angle = (System?.RandomInt(0, 3600) ?? 0) / 3600.0 * Math.Tau;
        float distance = radius * (System?.RandomInt(0, 1000) ?? 0) / 1000f;
        return (x + (distance * (float)Math.Cos(angle)), y + (distance * (float)Math.Sin(angle)), z);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (System is not { } system)
        {
            return;
        }

        foreach ((Creature who, uint point, float x, float y) in _moves.ToArray())
        {
            if (((who.X - x) * (who.X - x)) + ((who.Y - y) * (who.Y - y)) <= 1f)
            {
                _moves.RemoveAll(m => ReferenceEquals(m.Who, who) && m.Point == point);
                Arrived(who, point);
            }
        }

        if (_eventMs != 0)
        {
            if (_eventMs <= diffMs)
            {
                _eventMs = 0;
                switch (_eventPhase)
                {
                    case 0: // redeemed: Tyrande kneels, he lies down
                        if (_tyrande is not null)
                        {
                            system.InterruptCast(_tyrande);
                            _tyrande.StandState = StandState.Kneel;
                            system.SayText(_tyrande, EmoteTyrandeKneel);
                        }

                        if (_remulos?.Me is { } remulos)
                        {
                            system.SetFacingTo(remulos, MathF.Atan2(Me.Y - remulos.Y, Me.X - remulos.X));
                        }

                        system.SayText(Me, EmoteRedeem);
                        Me.StandState = StandState.Dead;
                        _eventMs = 5_000;
                        break;
                    case 1:
                        if (_tyrande is not null)
                        {
                            system.SayText(_tyrande, SayTyrandeRedeemed);
                        }

                        _eventMs = 6_000;
                        break;
                    case 2:
                        DoCast(Me, SpellEranikusRedeemed);
                        _eventMs = 5_000;
                        break;
                    case 3: // walk in front of Tyrande
                        Me.StandState = StandState.Stand;
                        (float rx, float ry, float rz, _) = KeeperRemulosAI.EranikusLocations[3];
                        MoveTracked(Me, PointRedeemed, rx, ry, rz, run: false);
                        break;
                    case 4:
                        system.SayText(Me, SayRedeemed2);
                        _eventMs = 11_000;
                        break;
                    case 5:
                        system.SayText(Me, SayRedeemed3);
                        _eventMs = 13_000;
                        break;
                    case 6:
                        system.SayText(Me, SayRedeemed4);
                        _eventMs = 7_000;
                        break;
                    case 7: // the quest is done
                        if (_tyrande is not null)
                        {
                            _tyrande.StandState = StandState.Stand;
                            system.ForcedDespawn(_tyrande, 9_000);
                        }

                        _remulos?.HandleOutro(Me);
                        system.PlayEmote(Me, EmoteOneshotBow);
                        system.ForcedDespawn(Me, 2_000);
                        break;
                }

                ++_eventPhase;
            }
            else
            {
                _eventMs -= diffMs;
            }
        }

        if (!InCombat())
        {
            return;
        }

        if (_tyrandeMoveMs != 0)
        {
            if (_tyrandeMoveMs <= diffMs)
            {
                if (_tyrande is not null)
                {
                    MoveTracked(_tyrande, 1, s_tyrande[2].X, s_tyrande[2].Y, s_tyrande[2].Z, run: true);
                }

                _tyrandeMoveMs = 0;
            }
            else
            {
                _tyrandeMoveMs -= diffMs;
            }
        }

        if (_healthCheck != 0 && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * (uint)_healthCheck)
        {
            switch (_healthCheck)
            {
                case 85: // Tyrande only calls out; summoned for a second for the yell
                    system.SayText(Me, SayAttack3);
                    if (system.SummonCorpseTimedDespawn(Me, NpcTyrande, s_tyrande[0].X, s_tyrande[0].Y, s_tyrande[0].Z, 0f, null, 0) is { } voice)
                    {
                        system.SayText(voice, SayTyrandeAppear);
                        system.ForcedDespawn(voice, 1_000);
                    }

                    _healthCheck = 75;
                    break;
                case 75:
                    system.SayText(Me, SayAttack3);
                    _healthCheck = 50;
                    break;
                case 50: // Tyrande joins
                    _tyrande = system.SummonCorpseTimedDespawn(Me, NpcTyrande, s_tyrande[0].X, s_tyrande[0].Y, s_tyrande[0].Z, 0f, null, 0);
                    if (_tyrande is not null)
                    {
                        _tyrande.SetUInt32(UpdateFields.UnitFieldMountdisplayid, PriestessMount);
                        MoveTracked(_tyrande, 0, s_tyrande[1].X, s_tyrande[1].Y, s_tyrande[1].Z, run: true);
                    }

                    _healthCheck = 35;
                    break;
                case 35: // the priestesses
                    for (int i = 0; i < MaxPriestess; i++)
                    {
                        (float px, float py, float pz) = RandomPoint(s_tyrande[0].X, s_tyrande[0].Y, s_tyrande[0].Z, 10f);
                        if (system.SummonCorpseTimedDespawn(Me, NpcElunePriestess, px, py, pz, 0f, null, 0) is { } priestess)
                        {
                            priestess.SetUInt32(UpdateFields.UnitFieldMountdisplayid, PriestessMount);
                            _priestesses.Add(priestess);
                            (float hx, float hy, float hz) = RandomPoint(s_tyrande[1].X, s_tyrande[1].Y, s_tyrande[1].Z, 10f);
                            MoveTracked(priestess, 0, hx, hy, hz, run: true);
                        }
                    }

                    system.SayText(Me, SayDefeat1);
                    _healthCheck = 31;
                    break;
                case 31:
                    if (_tyrande is not null)
                    {
                        system.SayText(_tyrande, SayTyrandeForgiven2);
                    }

                    _healthCheck = 27;
                    break;
                case 27:
                    if (_tyrande is not null)
                    {
                        system.SayText(_tyrande, SayTyrandeForgiven3);
                    }

                    _healthCheck = 25;
                    break;
                case 25:
                    system.SayText(Me, SayDefeat2);
                    _healthCheck = 20;
                    break;
                case 20: // redeemed: the fight stops
                    system.SayText(Me, SayDefeat3);
                    _healthCheck = 0;
                    system.EnterEvadeMode(Me);
                    return;
            }
        }

        CastWhenReady(ref _acidBreathMs, diffMs, Me, SpellAcidBreath, 15_000, 15_000);
        CastWhenReady(ref _noxiousBreathMs, diffMs, Me, SpellNoxiousBreath, 30_000, 30_000);
        CastWhenReady(ref _volleyMs, diffMs, Me, SpellShadowboltVolley, 25_000, 25_000);
    }
}

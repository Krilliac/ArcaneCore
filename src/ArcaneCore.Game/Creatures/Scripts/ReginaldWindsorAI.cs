using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Marshal Reginald Windsor (entry 12580, Stormwind), the escort of quest 6403 "The Great Masquerade": mangos-classic ScriptDev2
/// <c>npc_reginald_windsor</c> (eastern_kingdoms/stormwind_city.cpp at 3e8597afe7) on the z2815 path (27 points). Accepting plays his
/// lines and starts the walk; at the gates Jonathan and six royal guards greet him (point 2), the guards are dismissed at 23 and he waits
/// for the player's word (gossip) to enter the keep; in the throne room (26) Wrynn is taken away, Windsor reveals Prestor, who turns into
/// Onyxia, turns the nearby royal guards into Onyxia's guards and kills him; once those are dead Bolvar kneels at his side and the
/// quest is done. ScriptedMap's creature storage is the nearest creature of each entry on the map here.
/// </summary>
public sealed class ReginaldWindsorAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 12580, QuestTheGreatMasquerade = 6403, QuestStormwindRendezvous = 6402;
    public const uint NpcJonathan = 466, NpcWrynn = 1747, NpcBolvar = 1748, NpcPrestor = 1749;
    public const uint NpcGuardRoyal = 1756, NpcGuardCity = 68, NpcGuardPatroller = 1976, NpcGuardOnyxia = 12739;
    public const uint SpellWindsorInspiration = 20273, SpellOnyxiaTransform = 20409, SpellWindsorRead = 20358, SpellWindsorDeath = 20465,
        SpellOnyxiaDespawn = 20466, SpellHammerOfJustice = 10308, SpellShieldWall = 871, SpellStrongCleave = 8255;
    public const uint EmoteOneshotSalute = 66, EmoteOneshotKneel = 16, EmoteOneshotPoint = 25, EmoteOneshotLiftoff = 254;
    public const int SayPrestorKeep13 = -1000867, SayWindsorKeep16 = -1000870, EmoteWindsorDie = -1000871, EmoteGuardTransform = -1000872,
        EmotePrestorLaugh = -1000854;

    private enum Speaker { None, Windsor, Jonathan, Prestor, Bolvar }

    private static readonly (int Entry, Speaker Speaker, uint Delay)[] s_dialogue =
    [
        (-1000825, Speaker.Windsor, 7000), (-1000826, Speaker.Windsor, 6000), (-1000827, Speaker.Prestor, 0),
        (-1000828, Speaker.Jonathan, 5000), (-1000829, Speaker.Windsor, 6000), (-1000830, Speaker.Windsor, 5000),
        (-1000831, Speaker.Jonathan, 3000), (-1000832, Speaker.Jonathan, 6000), (-1000833, Speaker.Jonathan, 7000),
        (-1000834, Speaker.Windsor, 8000), (-1000835, Speaker.Windsor, 6000), (-1000836, Speaker.Jonathan, 7000),
        (-1000837, Speaker.Jonathan, 6000), (-1000838, Speaker.Jonathan, 5000), ((int)EmoteOneshotSalute, Speaker.None, 4000),
        (-1000839, Speaker.Jonathan, 3000), ((int)NpcJonathan, Speaker.None, 6000), ((int)EmoteOneshotKneel, Speaker.None, 3000),
        (-1000840, Speaker.Windsor, 5000), (-1000841, Speaker.Windsor, 3000), ((int)EmoteOneshotPoint, Speaker.None, 3000),
        ((int)Entry, Speaker.None, 0),
        ((int)NpcGuardRoyal, Speaker.None, 3000), (-1000849, Speaker.Windsor, 0),
        (-1000850, Speaker.Windsor, 4000), ((int)NpcGuardCity, Speaker.None, 0),
        ((int)NpcWrynn, Speaker.None, 3000), (-1000851, Speaker.Windsor, 3000), (-1000852, Speaker.Bolvar, 2000),
        (-1000853, Speaker.Windsor, 4000), (EmotePrestorLaugh, Speaker.Prestor, 4000), (-1000855, Speaker.Prestor, 9000),
        (-1000856, Speaker.Prestor, 7000), (-1000857, Speaker.Windsor, 6000), (-1000858, Speaker.Windsor, 6000),
        (-1000859, Speaker.Windsor, 4000), (-1000860, Speaker.Windsor, 5000), (-1000861, Speaker.Windsor, 3000),
        ((int)SpellWindsorRead, Speaker.None, 10000), (-1000862, Speaker.Bolvar, 3000), (-1000863, Speaker.Prestor, 4000),
        (-1000864, Speaker.Bolvar, 3000), (-1000865, Speaker.Prestor, 2000), ((int)SpellWindsorDeath, Speaker.None, 1500),
        (-1000866, Speaker.Windsor, 4000), (-1000868, Speaker.Prestor, 0),
        ((int)NpcGuardOnyxia, Speaker.None, 14000), ((int)NpcBolvar, Speaker.None, 2000), (-1000869, Speaker.Bolvar, 8000),
        ((int)NpcGuardPatroller, Speaker.None, 0),
    ];

    private static readonly (float X, float Y, float Z, float O)[] s_guardLocations =
    [
        (-8968.510f, 512.556f, 96.352f, 3.849f), (-8969.780f, 515.012f, 96.593f, 3.955f), (-8972.410f, 518.228f, 96.594f, 4.281f),
        (-8965.170f, 508.565f, 96.352f, 3.825f), (-8962.960f, 506.583f, 96.593f, 3.802f), (-8961.080f, 503.828f, 96.593f, 3.465f),
    ];

    private static readonly (float X, float Y, float Z)[] s_moveLocations =
    [
        (-8967.960f, 510.008f, 96.351f), (-8959.440f, 505.424f, 96.595f), (-8957.670f, 507.056f, 96.595f), (-8970.680f, 519.252f, 96.595f),
        (-8969.100f, 520.395f, 96.595f), (-8974.590f, 516.213f, 96.590f), (-8505.770f, 338.312f, 120.886f), (-8448.690f, 337.074f, 121.330f),
        (-8448.279f, 338.398f, 121.329f),
    ];

    private uint _guardCheckMs, _hammerMs, _cleaveMs, _stepMs;
    private int _step = -1;
    private bool _keepReady;
    private ObjectGuid _playerGuid;
    private readonly Creature?[] _guards = new Creature?[6];
    private readonly List<Creature> _royalGuards = [];
    private readonly Dictionary<Creature, (int Point, float X, float Y)> _guardMoves = new(ReferenceEqualityComparer.Instance);

    public bool KeepEventReady => _keepReady;
    public int DialogueStep => _step;
    public IReadOnlyList<Creature> RoyalGuards => _royalGuards;

    /// <summary>Kept at his last point until the throne room scene lets him go (SetEscortPaused(false) at NPC_GUARD_PATROLLER).</summary>
    protected override bool HoldAtEndWhilePaused => true;

    /// <summary>The constructor: his quest giver flag is the script's.</summary>
    protected override void JustSpawned() => Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;

    /// <summary>
    /// npc_reginald_windsorAI::Reset rerolls only the combat timers. The keep word and the guard check are the scene's, set in the
    /// constructor: an evade (he can be drawn into the turned guards' fight) must not take back the word or stop the check that sends
    /// Bolvar to him once the guards are dead.
    /// </summary>
    protected override void Reset()
    {
        _hammerMs = (uint)(System?.RandomInt(0, 1000) ?? 0);
        _cleaveMs = (uint)(System?.RandomInt(1000, 3000) ?? 1000);
    }

    /// <summary>A respawned Windsor starts the event over: no stale keep word or guard check survives his death (port choice; Reset no longer clears them).</summary>
    protected override void JustRespawned()
    {
        _guardCheckMs = 0;
        _keepReady = false;
        base.JustRespawned();
    }

    protected override void Aggro(Unit target) => DoCast(Me, SpellShieldWall);

    /// <summary>QuestAccept_npc_reginald_windsor: DoStartEscort.</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestTheGreatMasquerade)
        {
            return;
        }

        _playerGuid = player.Guid;
        StartDialogue(0);
    }

    /// <summary>GossipSelect: DoStartKeepEvent.</summary>
    public void StartKeepEvent()
    {
        StartDialogue(Array.FindIndex(s_dialogue, d => d.Entry == -1000850));
        Me.NpcFlags &= ~(uint)NpcFlags.Gossip;
    }

    private Creature? Stored(uint entry) => System?.Creatures.Where(c => c.Entry == entry && c.Map == Me.Map)
        .OrderBy(c => ((c.X - Me.X) * (c.X - Me.X)) + ((c.Y - Me.Y) * (c.Y - Me.Y))).FirstOrDefault();

    private void Face(Creature who, WorldObject at) => System?.SetFacingTo(who, MathF.Atan2(at.Y - who.Y, at.X - who.X));

    private void MoveTo(Creature? who, int loc, bool run = false)
    {
        if (who is not null)
        {
            System?.MoveTo(who, s_moveLocations[loc].X, s_moveLocations[loc].Y, s_moveLocations[loc].Z, run, finalOrientation: null);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (Stored(NpcJonathan) is { } jonathan && System is { } system)
                {
                    for (int i = 0; i < _guards.Length; i++)
                    {
                        (float x, float y, float z, float o) = s_guardLocations[i];
                        // TEMPSPAWN_TIMED_DESPAWN, 180000
                        _guards[i] = system.SummonAt(Me, NpcGuardRoyal, x, y, z, o, null, 180_000);
                    }

                    jonathan.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
                    MoveTo(jonathan, 0, run: true);
                }

                break;
            case 2:
                StartDialogue(Array.FindIndex(s_dialogue, d => d.Entry == -1000828));
                SetEscortPaused(true);
                break;
            case 4:
                DoCast(Me, SpellWindsorInspiration, triggered: true);
                break;
            case 12:
                if (Stored(NpcJonathan) is { } returning)
                {
                    returning.StandState = StandState.Stand;
                    returning.Motion.MoveTargetedHome(returning.Home);
                }

                break;
            case 23:
                SetEscortPaused(true);
                StartDialogue(Array.FindIndex(s_dialogue, d => d.Entry == (int)NpcGuardRoyal));
                break;
            case 26:
                StartDialogue(Array.FindIndex(s_dialogue, d => d.Entry == (int)NpcWrynn));
                SetEscortPaused(true);
                break;
        }
    }

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

        if (step.Speaker != Speaker.None)
        {
            Creature? speaker = step.Speaker switch
            {
                Speaker.Windsor => Me,
                Speaker.Jonathan => Stored(NpcJonathan),
                Speaker.Prestor => Stored(NpcPrestor),
                _ => Stored(NpcBolvar),
            };
            if (speaker is not null)
            {
                system.SayText(speaker, step.Entry);
            }
        }

        Creature? jonathan = Stored(NpcJonathan), prestor = Stored(NpcPrestor), bolvar = Stored(NpcBolvar), wrynn = Stored(NpcWrynn);
        switch (step.Entry)
        {
            case -1000826: // SAY_WINDSOR_GET_READY
                system.SetFacingTo(Me, 0.6f);
                break;
            case -1000827: // SAY_PRESTOR_SIEZE
                if (system.Map.FindPlayer(_playerGuid) is { } player)
                {
                    Start(run: false, player: player, questId: QuestTheGreatMasquerade);
                }

                break;
            case -1000836: // SAY_JON_DIALOGUE_8: left guards
                if (jonathan is not null)
                {
                    system.SetFacingTo(jonathan, 5.375f);
                }

                Kneel(_guards[5], 2.234f);
                GuardMove(_guards[4], 1);
                GuardMove(_guards[3], 2);
                break;
            case -1000837: // SAY_JON_DIALOGUE_9: right guards
                if (jonathan is not null)
                {
                    system.SetFacingTo(jonathan, 2.234f);
                }

                Kneel(_guards[2], 5.375f);
                GuardMove(_guards[1], 3);
                GuardMove(_guards[0], 4);
                break;
            case -1000838: // SAY_JON_DIALOGUE_10
                if (jonathan is not null)
                {
                    Face(jonathan, Me);
                }

                break;
            case (int)EmoteOneshotSalute:
                if (jonathan is not null)
                {
                    system.PlayEmote(jonathan, EmoteOneshotSalute);
                }

                break;
            case (int)NpcJonathan:
                MoveTo(jonathan, 5);
                break;
            case (int)EmoteOneshotKneel:
                if (jonathan is not null)
                {
                    Face(jonathan, Me);
                    jonathan.StandState = StandState.Kneel;
                }

                break;
            case -1000840: // SAY_WINDSOR_DIALOGUE_12
                if (jonathan is not null)
                {
                    Face(Me, jonathan);
                }

                break;
            case -1000841: // SAY_WINDSOR_DIALOGUE_13
                system.SetFacingTo(Me, 0.08f);
                break;
            case (int)EmoteOneshotPoint:
                system.PlayEmote(Me, EmoteOneshotPoint);
                break;
            case (int)Entry: // NPC_WINDSOR: the guards stop cheering, on he goes
                system.RemoveAuras(Me, SpellWindsorInspiration);
                SetEscortPaused(false);
                break;
            case -1000849: // SAY_WINDSOR_BEFORE_KEEP
                _keepReady = true;
                Me.NpcFlags |= (uint)NpcFlags.Gossip;
                break;
            case (int)NpcGuardCity:
                SetEscortPaused(false);
                break;
            case (int)NpcWrynn:
                if (prestor is not null)
                {
                    prestor.NpcFlags &= ~(uint)(NpcFlags.Gossip | NpcFlags.QuestGiver);
                }

                if (wrynn is not null)
                {
                    wrynn.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
                }

                if (bolvar is not null)
                {
                    bolvar.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
                }

                break;
            case -1000852: // SAY_BOLVAR_KEEP_2: Wrynn is taken to safety; the royal guards near him are kept for the reveal
                if (wrynn is not null)
                {
                    system.ForcedDespawn(wrynn, 15_000);
                    MoveTo(wrynn, 6, run: true);
                    _royalGuards.Clear();
                    _royalGuards.AddRange(system.Creatures.Where(c => c.Entry == NpcGuardRoyal && c.IsAlive
                        && ((c.X - wrynn.X) * (c.X - wrynn.X)) + ((c.Y - wrynn.Y) * (c.Y - wrynn.Y)) + ((c.Z - wrynn.Z) * (c.Z - wrynn.Z)) <= 25f * 25f));
                }

                break;
            case (int)SpellWindsorRead:
                DoCast(Me, SpellWindsorRead);
                break;
            case -1000862: // EMOTE_BOLVAR_GASP: Prestor shows herself
                if (prestor is not null)
                {
                    system.CastSpell(prestor, SpellOnyxiaTransform, prestor, false);
                    if (bolvar is not null)
                    {
                        Face(bolvar, prestor);
                    }
                }

                break;
            case -1000863: // SAY_PRESTOR_KEEP_9
                MoveTo(bolvar, 7, run: true);
                break;
            case -1000864: // SAY_BOLVAR_KEEP_10
                if (bolvar is not null)
                {
                    if (prestor is not null)
                    {
                        Face(bolvar, prestor);
                        system.SayText(prestor, EmotePrestorLaugh);
                    }

                    bolvar.FactionTemplate = 11; // TEMPFACTION_RESTORE_REACH_HOME: he can fight Onyxia's guards
                }

                break;
            case -1000865: // SAY_PRESTOR_KEEP_11: the guards turn
                foreach (Creature guard in _royalGuards.Where(g => g.IsAlive))
                {
                    system.UpdateEntry(guard, NpcGuardOnyxia);
                    system.SayText(guard, EmoteGuardTransform);
                    if (bolvar is not null)
                    {
                        system.AttackStart(guard, bolvar);
                    }
                }

                _guardCheckMs = 1000;
                break;
            case (int)SpellWindsorDeath:
                if (prestor is not null)
                {
                    system.CastSpell(prestor, SpellWindsorDeath, Me, false);
                }

                break;
            case -1000866: // SAY_WINDSOR_KEEP_12: Prestor's taunt, and he lies as dead
                if (prestor is not null)
                {
                    system.SayText(prestor, SayPrestorKeep13);
                }

                // He lies as dead (SPELL_WINDSOR_DEATH's feign): nothing can finish him off before Bolvar reaches him.
                Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToNpc | UnitFlags.ImmuneToPlayer;
                Me.StandState = StandState.Dead;
                break;
            case -1000868: // SAY_PRESTOR_KEEP_14: she flies off
                if (prestor is not null)
                {
                    system.ForcedDespawn(prestor, 1000);
                    system.PlayEmote(prestor, EmoteOneshotLiftoff);
                    system.CastSpell(prestor, SpellOnyxiaDespawn, prestor, false);
                }

                break;
            case (int)NpcGuardOnyxia:
                MoveTo(bolvar, 7);
                break;
            case (int)NpcBolvar:
                MoveTo(bolvar, 8);
                break;
            case -1000869: // SAY_BOLVAR_KEEP_15
                if (bolvar is not null)
                {
                    bolvar.StandState = StandState.Kneel;
                }

                system.SayText(Me, SayWindsorKeep16);
                system.SayText(Me, EmoteWindsorDie);
                if (system.Map.FindPlayer(_playerGuid) is { } rewarded)
                {
                    system.RewardGroupEventExplored(rewarded, QuestTheGreatMasquerade, Me);
                }

                break;
            case (int)NpcGuardPatroller: // reset Bolvar and Wrynn; Onyxia comes back by herself
                if (bolvar is not null)
                {
                    bolvar.NpcFlags |= (uint)NpcFlags.QuestGiver;
                    bolvar.StandState = StandState.Stand;
                    bolvar.FactionTemplate = bolvar.Template.Faction;
                    bolvar.Motion.MoveTargetedHome(bolvar.Home);
                }

                if (wrynn is not null)
                {
                    wrynn.NpcFlags |= (uint)NpcFlags.QuestGiver;
                    system.ForceRespawn(wrynn);
                }

                if (prestor is not null)
                {
                    prestor.NpcFlags |= (uint)(NpcFlags.Gossip | NpcFlags.QuestGiver);
                }

                SetEscortPaused(false);
                break;
        }
    }

    private void Kneel(Creature? guard, float facing)
    {
        if (guard is not null)
        {
            System?.SetFacingTo(guard, facing);
            guard.StandState = StandState.Kneel;
        }
    }

    private void GuardMove(Creature? guard, int point)
    {
        if (guard is null)
        {
            return;
        }

        MoveTo(guard, point);
        _guardMoves[guard] = (point, s_moveLocations[point].X, s_moveLocations[point].Y);
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        // SummonedMovementInform for the royal guards at the gates: kneel facing the road.
        foreach ((Creature guard, (int point, float x, float y)) in _guardMoves.ToArray())
        {
            if (((guard.X - x) * (guard.X - x)) + ((guard.Y - y) * (guard.Y - y)) <= 1f)
            {
                _guardMoves.Remove(guard);
                Kneel(guard, point <= 2 ? 2.234f : 5.375f);
            }
        }

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

        if (_guardCheckMs != 0)
        {
            if (_guardCheckMs <= diffMs)
            {
                if (_royalGuards.All(g => !g.IsAlive && g.Entry == NpcGuardOnyxia))
                {
                    StartDialogue(Array.FindIndex(s_dialogue, d => d.Entry == (int)NpcGuardOnyxia));
                    _guardCheckMs = 0;
                }
                else
                {
                    _guardCheckMs = 1000;
                }
            }
            else
            {
                _guardCheckMs -= diffMs;
            }
        }

        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (_hammerMs < diffMs)
        {
            if (DoCast(victim, SpellHammerOfJustice) == CreatureCastResult.Ok)
            {
                _hammerMs = 60_000;
            }
        }
        else
        {
            _hammerMs -= diffMs;
        }

        if (_cleaveMs < diffMs)
        {
            if (DoCast(victim, SpellStrongCleave) == CreatureCastResult.Ok)
            {
                _cleaveMs = (uint)(System?.RandomInt(1000, 5000) ?? 1000);
            }
        }
        else
        {
            _cleaveMs -= diffMs;
        }
    }
}

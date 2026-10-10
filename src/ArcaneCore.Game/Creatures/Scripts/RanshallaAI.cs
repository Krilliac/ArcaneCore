using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Priestess Ranshalla (entry 10300, Winterspring), the escort of quest 4901 "Guardians of the Altar": mangos-classic ScriptDev2
/// <c>npc_ranshalla</c> and <c>go_elune_fire</c> (kalimdor/winterspring.cpp at a57aa7f074) on the z2815 path (44 points). At five caverns
/// she lights a torch (Elune's Fire, 177417) and waits until a player uses it; at the altar (177404) the same, then the long altar scene
/// with two Priestesses of Elune, the Voice and the Guardian of Elune, and the credit at its end. The relit gem, aura and lights are
/// respawned with ForceRespawn (their 90/115 s despawn again is not scheduled). Summon arrivals are read by distance each update.
/// </summary>
public sealed class RanshallaAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 10300, QuestGuardiansAltar = 4901, FactionEscortAllianceNeutralPassive = 10;
    public const uint NpcPriestessElune = 12116, NpcVoiceElune = 12152, NpcGuardianElune = 12140;
    public const uint GoEluneAltar = 177404, GoEluneFire = 177417, GoEluneGem = 177414, GoEluneLight = 177415, GoEluneAura = 177416;
    public const uint SpellLightTorch = 18953, SpellBindWildkin = 18994;
    public const int SayQuestStart = -1000739, SayEnterOwlThicket = -1000707, SayAfterTorch1 = -1000711, SayAfterTorch2 = -1000712,
        SayRanshallaAltar1 = -1000715, SayRanshallaAltar2 = -1000716, SayPriestessAltar14 = -1000728, SayQuestEnd1 = -1000736,
        EmoteChantSpell = -1000738;
    public static readonly int[] SayReachTorch = [-1000708, -1000709, -1000710];

    private enum Speaker { None, Ranshalla, Priestess1, Priestess2, Voice }

    // aIntroDialogue: (entry, speaker, delay); a negative entry with a speaker is said by it, the rest are the script's step tags.
    private const int TagRanshalla = 10300, TagGem = 177414, TagPriestess1 = 1, TagPriestess2 = 2, TagVoice = 12152, TagGuardian = 12140,
        TagMovePriestess = 3, TagEventEnd = 4;
    private static readonly (int Entry, Speaker Speaker, uint Delay)[] s_dialogue =
    [
        (-1000713, Speaker.Ranshalla, 2000), (-1000714, Speaker.Ranshalla, 3000), (TagRanshalla, Speaker.None, 0),
        (-1000717, Speaker.Priestess2, 1000), (-1000718, Speaker.Priestess1, 4000), (-1000719, Speaker.Ranshalla, 4000),
        (-1000720, Speaker.Ranshalla, 4000), (-1000721, Speaker.Priestess2, 4000), (-1000722, Speaker.Priestess2, 5000),
        (TagGem, Speaker.None, 5000), (-1000723, Speaker.Priestess1, 4000), (TagPriestess1, Speaker.None, 3000),
        (-1000724, Speaker.Priestess1, 5000), (-1000725, Speaker.Priestess1, 4000), (-1000726, Speaker.Priestess1, 5000),
        (-1000727, Speaker.Priestess1, 8000), (TagVoice, Speaker.None, 12000), (-1000729, Speaker.Voice, 5000),
        (TagPriestess2, Speaker.None, 3000), (-1000730, Speaker.Priestess2, 4000), (-1000731, Speaker.Priestess2, 6000),
        (-1000732, Speaker.Priestess1, 5000), (-1000733, Speaker.Priestess1, 3000), (TagGuardian, Speaker.None, 2000),
        (-1000734, Speaker.Priestess1, 4000), (-1000735, Speaker.Priestess2, 10000), (TagMovePriestess, Speaker.None, 6000),
        (TagEventEnd, Speaker.None, 2000), (-1000737, Speaker.Ranshalla, 0),
    ];

    private static readonly (float X, float Y, float Z, float O)[] s_locs =
    [
        (5515.98f, -4903.43f, 846.30f, 4.58f), (5501.94f, -4920.20f, 848.69f, 6.15f), (5497.35f, -4906.49f, 850.83f, 2.76f),
        (5518.38f, -4913.47f, 845.57f, 0f), (5510.36f, -4921.17f, 846.33f, 0f), (5511.31f, -4913.82f, 847.17f, 0f),
        (5518.51f, -4917.56f, 845.23f, 0f), (5514.40f, -4921.16f, 845.49f, 0f),
    ];

    private uint _delayMs, _currentWaypoint, _stepMs;
    private int _step = -1;
    private bool _waitingForPriestess;
    private Creature? _priestess1, _priestess2, _guardian, _voice;
    private GameObject? _altar;

    public int DialogueStep => _step;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestGuardiansAltar)
        {
            return;
        }

        System?.SayText(Me, SayQuestStart);
        Me.FactionTemplate = FactionEscortAllianceNeutralPassive;
        Start(run: false, instantRespawn: true, player: player, questId: questId);
    }

    /// <summary>The fire and altar scripts live with her: registered on the map's objects when she is placed (and again at a torch).</summary>
    protected override void JustSpawned() => RegisterObjects();

    private void RegisterObjects()
    {
        if (System?.Map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            objects.RegisterAi(GoEluneFire, new EluneFireAi());
            objects.RegisterAi(GoEluneAltar, new EluneFireAi());
        }
    }

    protected override void Reset()
    {
        _delayMs = 0;
        _currentWaypoint = 0;
    }

    private GameObjectMapSystem? Objects => System?.Map.FindUpdater<GameObjectMapSystem>();

    private GameObject? ClosestObject(uint entry, float range) => Objects?.GameObjects
        .Where(g => g.Entry == entry && Dist(g) <= range).OrderBy(Dist).FirstOrDefault();

    private float Dist(WorldObject o)
    {
        float dx = Me.X - o.X, dy = Me.Y - o.Y, dz = Me.Z - o.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private void Face(WorldObject o) => System?.SetFacingTo(Me, MathF.Atan2(o.Y - Me.Y, o.X - Me.X));

    /// <summary>DoContinueEscort: a torch or the altar was lit; two seconds later she walks on.</summary>
    public void ContinueEscort(bool altar)
    {
        if (System is not { } system)
        {
            return;
        }

        system.SayText(Me, altar ? SayRanshallaAltar1 : (system.RandomInt(0, 1) == 0 ? SayAfterTorch1 : SayAfterTorch2));
        _delayMs = 2000;
    }

    private void DoChannelTorchSpell(bool altar = false)
    {
        if (System is not { } system)
        {
            return;
        }

        RegisterObjects();
        if (altar)
        {
            if (ClosestObject(GoEluneAltar, 10f) is { } go)
            {
                go.Flags &= ~GameObjectFlags.NoInteract;
                Face(go);
                _altar = go;
            }
        }
        else if (ClosestObject(GoEluneFire, 10f) is { } fire)
        {
            fire.Flags &= ~GameObjectFlags.NoInteract;
        }

        system.SayText(Me, SayReachTorch[system.RandomInt(0, 2)]);
        system.SayText(Me, EmoteChantSpell);
        DoCast(Me, SpellLightTorch);
        SetEscortPaused(true);
    }

    private Creature? SummonAndMove(uint entry, int at, int to, uint despawnMs = 0)
    {
        (float x, float y, float z, float o) = s_locs[at];
        if (System?.SummonAt(Me, entry, x, y, z, o, null, despawnMs) is not { } summoned)
        {
            return null;
        }

        if (to >= 0)
        {
            System.MoveTo(summoned, s_locs[to].X, s_locs[to].Y, s_locs[to].Z, run: false, finalOrientation: null);
        }

        return summoned;
    }

    private void MoveTo(Creature? who, int to)
    {
        if (who is not null)
        {
            System?.MoveTo(who, s_locs[to].X, s_locs[to].Y, s_locs[to].Z, run: false, finalOrientation: null);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 3:
                System?.SayText(Me, SayEnterOwlThicket);
                break;
            case 10 or 15 or 20 or 25 or 36:
                _currentWaypoint = pointId;
                DoChannelTorchSpell();
                break;
            case 39:
                _currentWaypoint = pointId;
                StartDialogue(0);
                SetEscortPaused(true);
                break;
            case 41:
                foreach (GameObject light in Objects?.GameObjects.Where(g => g.Entry == GoEluneLight && Dist(g) <= 20f && !g.IsSpawned).ToArray() ?? [])
                {
                    Objects!.ForceRespawn(light);
                }

                if (_altar is not null)
                {
                    Face(_altar);
                }

                break;
            case 42:
                SetEscortPaused(true);
                _priestess1 = SummonAndMove(NpcPriestessElune, 0, 3);
                _priestess2 = SummonAndMove(NpcPriestessElune, 1, 4);
                _waitingForPriestess = _priestess2 is not null; // SummonedMovementInform(point 1): the left one's arrival
                System?.SayText(Me, SayRanshallaAltar2);
                break;
            case 44:
                SetEscortPaused(true);
                if (_altar is not null)
                {
                    Face(_altar);
                }

                break;
        }
    }

    private void StartDialogue(int index)
    {
        _step = index;
        _stepMs = s_dialogue[index].Delay;
        DoDialogueStep(s_dialogue[index]);
    }

    private void DoDialogueStep((int Entry, Speaker Speaker, uint Delay) step)
    {
        if (System is not { } system)
        {
            return;
        }

        if (step.Speaker != Speaker.None)
        {
            Creature? speaker = step.Speaker switch
            {
                Speaker.Ranshalla => Me,
                Speaker.Priestess1 => _priestess1,
                Speaker.Priestess2 => _priestess2,
                _ => _voice,
            };
            if (speaker is not null)
            {
                system.SayText(speaker, step.Entry);
            }
        }

        switch (step.Entry)
        {
            case TagRanshalla:
                DoChannelTorchSpell(altar: true);
                break;
            case -1000720: // SAY_RANSHALLA_ALTAR_6
                SetEscortPaused(false);
                break;
            case -1000722: // SAY_PRIESTESS_ALTAR_8: show the gem and its aura
                foreach (uint entry in (uint[])[GoEluneGem, GoEluneAura])
                {
                    if (ClosestObject(entry, 10f) is { IsSpawned: false } go)
                    {
                        Objects!.ForceRespawn(go);
                    }
                }

                break;
            case -1000723: // SAY_PRIESTESS_ALTAR_9
                MoveTo(_priestess1, 6);
                break;
            case -1000727: // SAY_PRIESTESS_ALTAR_13: the guardian and the voice
                _guardian = SummonAndMove(NpcGuardianElune, 2, 5);
                if (_altar is not null)
                {
                    _voice = system.SummonAt(Me, NpcVoiceElune, _altar.X, _altar.Y, _altar.Z, 0f, null, 30_000);
                }

                break;
            case -1000729: // SAY_VOICE_ALTAR_15
                if (_priestess2 is not null)
                {
                    system.SayText(_priestess2, SayPriestessAltar14);
                    MoveTo(_priestess2, 7);
                }

                if (_guardian is not null)
                {
                    system.CastSpell(_guardian, SpellBindWildkin, _guardian, false);
                }

                break;
            case -1000733: // SAY_PRIESTESS_ALTAR_19
                MoveTo(_guardian, 2);
                if (_guardian is not null)
                {
                    system.ForcedDespawn(_guardian, 4000);
                }

                break;
            case -1000734: // SAY_PRIESTESS_ALTAR_20
                MoveTo(_priestess1, 0);
                if (_priestess1 is not null)
                {
                    system.ForcedDespawn(_priestess1, 4000);
                }

                break;
            case -1000735: // SAY_PRIESTESS_ALTAR_21
                MoveTo(_priestess2, 1);
                if (_priestess2 is not null)
                {
                    system.ForcedDespawn(_priestess2, 4000);
                }

                break;
            case TagEventEnd:
                if (GetPlayerForEscort() is { } player)
                {
                    Face(player);
                    system.SayText(Me, SayQuestEnd1, player);
                }

                break;
            case -1000737: // SAY_QUEST_END_2: kneel at the altar, credit, gone in a minute
                if (_altar is not null)
                {
                    Face(_altar);
                }

                Me.StandState = StandState.Kneel;
                if (GetPlayerForEscort() is { } rewarded)
                {
                    system.RewardGroupEventExplored(rewarded, QuestGuardiansAltar, Me);
                }

                system.ForcedDespawn(Me, 60_000);
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_waitingForPriestess && _priestess2 is { } left)
        {
            float dx = left.X - s_locs[4].X, dy = left.Y - s_locs[4].Y;
            if ((dx * dx) + (dy * dy) <= 1f)
            {
                _waitingForPriestess = false;
                StartDialogue(3); // SAY_PRIESTESS_ALTAR_3
            }
        }

        // DialogueUpdate: a step with delay 0 ends the run of steps until the next StartNextDialogueText.
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

        if (_delayMs != 0)
        {
            if (_delayMs <= diffMs)
            {
                SetEscortPaused(false);
                _delayMs = 0;
            }
            else
            {
                _delayMs -= diffMs;
            }
        }

        UpdateVictim();
    }

    /// <summary>GOUse_go_elune_fire: Ranshalla within 10 yd walks on; the use goes on as usual.</summary>
    private sealed class EluneFireAi : IGameObjectAi
    {
        public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

        public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
        {
        }

        public bool OnUse(GameObjectMapSystem objects, GameObject go, Unit user)
        {
            RanshallaAI? ranshalla = go.Map?.FindUpdater<CreatureMapSystem>()?.Creatures
                .Where(c => c.IsAlive && c.Entry == Entry && ((c.X - go.X) * (c.X - go.X)) + ((c.Y - go.Y) * (c.Y - go.Y)) + ((c.Z - go.Z) * (c.Z - go.Z)) <= 100f)
                .Select(c => c.AI).OfType<RanshallaAI>().FirstOrDefault();
            ranshalla?.ContinueEscort(go.Entry == GoEluneAltar);
            return false;
        }
    }
}

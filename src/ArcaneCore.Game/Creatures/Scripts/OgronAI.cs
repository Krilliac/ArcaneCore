using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Ogron (entry 4983, Dustwallow Marsh), "Questioning Reethe" (quest 1273): mangos-classic ScriptDev2 <c>npc_ogron</c>
/// (kalimdor/dustwallow_marsh.cpp:164-466, registered :1233-1236) on <see cref="EscortAI"/>. Taking the quest starts his walk (he comes back at once
/// when it ends) with the neutral friendly passive escort faction (290, not immune to NPCs) until respawn and his start line. The first Paval Reethe
/// (4980) he sees on the way is his man; at point 10 he spots him, at 11 Reethe asks what he wants and at 12 Ogron stops for the questioning: one
/// step every 5 s (the intro lines, then Captain Caldwell (5046), Private Hallan (5045) and two Theramore Skirmishers (5044) come up friendly for five
/// minutes; Caldwell's lines, Hallan's line and Fake Shot (7105) at Reethe, Reethe's arrow lines, the clean-up), after which the soldiers turn
/// hostile (151) on Ogron. When Ogron has won (his evade while still paused), the group gets the quest event and Reethe talks, and Ogron thanks the
/// player and runs back.
/// </summary>
public sealed class OgronAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 4983, QuestQuestioning = 1273, FactionEscortNeutralFriendPassive = 290, FactionGenericFriendly = 35, FactionTheramoreHostile = 151;
    public const uint NpcReethe = 4980, NpcCaldwell = 5046, NpcHallan = 5045, NpcSkirmisher = 5044, SpellFakeShot = 7105;
    public const uint StepMs = 5000, GuestLifetimeMs = 300_000;
    public const int SayStart = -1000452, SaySpot = -1000453, SayRetWhat = -1000454, SayRetSwear = -1000455, SayReplyRet = -1000456;
    public const int SayRetTaken = -1000457, SayTellFire = -1000458, SayRetNoCloser = -1000459, SayRetNoFire = -1000460, SayRetHear = -1000461;
    public const int SayCalFound = -1000462, SayCalMercy = -1000463, SayHallGlad = -1000464, EmoteRetArrow = -1000465, SayRetArrow = -1000466;
    public const int SayCalCleanup = -1000467, SayNoDie = -1000468, SaySurvive = -1000469, SayRetLucky = -1000470, SayThanks = -1000471;

    /// <summary>m_afSpawn and m_afMoveTo (:209-210): where the soldiers appear and where Caldwell walks.</summary>
    public static readonly (float X, float Y, float Z) GuestSpawn = (-3383.501953f, -3203.383301f, 36.149f), CaldwellMoveTo = (-3371.414795f, -3212.179932f, 34.210f);

    public enum Phase
    {
        Intro = 0,
        Guests = 1,
        Fight = 2,
        Complete = 3,
    }

    private readonly List<ObjectGuid> _creatures = [];
    private Phase _phase;
    private uint _phaseCounter, _globalMs = StepMs;

    public Phase CurrentPhase => _phase;

    public uint PhaseCounter => _phaseCounter;

    /// <summary>Reset (:228-241): an evade while paused in the fight completes it; outside an escort everything starts over.</summary>
    protected override void Reset()
    {
        _globalMs = StepMs;
        if (HasEscortState(EscortState.Paused) && _phase == Phase.Fight)
        {
            _phase = Phase.Complete;
        }

        if (!HasEscortState(EscortState.Escorting))
        {
            _creatures.Clear();
            _phase = Phase.Intro;
            _phaseCounter = 0;
        }
    }

    /// <summary>QuestAccept_npc_ogron (:448-461).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestQuestioning)
        {
            return;
        }

        Start(run: false, instantRespawn: true, player: player, questId: questId);
        Me.SetFactionTemporary(FactionEscortNeutralFriendPassive, TemporaryFactionFlags.RestoreRespawn | TemporaryFactionFlags.ToggleImmuneToNpc);
        System?.SayText(Me, SayStart, player);
    }

    /// <summary>MoveInLineOfSight (:243-249): the first Reethe seen while escorting.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (HasEscortState(EscortState.Escorting) && who is Creature { Entry: NpcReethe } reethe && _creatures.Count == 0)
        {
            _creatures.Add(reethe.Guid);
        }

        base.MoveInLineOfSight(who);
    }

    /// <summary>GetCreature (:251-263): the first living creature of the entry in the list.</summary>
    private Creature? GetCreature(uint entry)
    {
        foreach (ObjectGuid guid in _creatures)
        {
            if (System?.FindCreature(guid) is { IsAlive: true } c && c.Entry == entry)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>WaypointReached (:265-280).</summary>
    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 10:
                System?.SayText(Me, SaySpot);
                break;
            case 11:
                if (GetCreature(NpcReethe) is { } reethe)
                {
                    System?.SayText(reethe, SayRetWhat);
                }

                break;
            case 12:
                SetEscortPaused(true);
                break;
        }
    }

    /// <summary>JustSummoned (:282-299): friendly; Caldwell walks up, the others follow him around.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        _creatures.Add(summoned.Guid);
        summoned.FactionTemplate = FactionGenericFriendly;
        if (summoned.Entry == NpcCaldwell)
        {
            summoned.Motion.MovePoint(0, CaldwellMoveTo.X, CaldwellMoveTo.Y, CaldwellMoveTo.Z, run: false);
        }
        else if (GetCreature(NpcCaldwell) is { } caldwell)
        {
            summoned.Motion.MoveFollow(caldwell, 0.5f, MathF.PI / 2f * _creatures.Count);
        }
    }

    /// <summary>DoStartAttackMe (:301-317): every living soldier turns hostile on Ogron.</summary>
    private void StartAttackMe()
    {
        foreach (ObjectGuid guid in _creatures.ToArray())
        {
            if (System?.FindCreature(guid) is { IsAlive: true } c && c.Entry != NpcReethe)
            {
                c.FactionTemplate = FactionTheramoreHostile;
                c.AI?.AttackStart(Me);
            }
        }
    }

    private void Say(uint speakerEntry, int textId)
    {
        if (GetCreature(speakerEntry) is { } speaker)
        {
            System?.SayText(speaker, textId);
        }
    }

    /// <summary>UpdateEscortAI (:319-445): out of a fight and paused, one step of the questioning every 5 s.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (UpdateVictim() && Victim is not null)
        {
            return;
        }

        if (!HasEscortState(EscortState.Paused))
        {
            return;
        }

        if (_globalMs >= diffMs)
        {
            _globalMs -= diffMs;
            return;
        }

        _globalMs = StepMs;
        DoStep();
        if (_phase != Phase.Fight)
        {
            ++_phaseCounter;
        }
    }

    private void DoStep()
    {
        if (System is not { } system)
        {
            return;
        }

        switch (_phase, _phaseCounter)
        {
            case (Phase.Intro, 0):
                Say(NpcReethe, SayRetSwear);
                break;
            case (Phase.Intro, 1):
                system.SayText(Me, SayReplyRet);
                break;
            case (Phase.Intro, 2):
                Say(NpcReethe, SayRetTaken);
                break;
            case (Phase.Intro, 3):
                system.SayText(Me, SayTellFire);
                Say(NpcReethe, SayRetNoCloser);
                break;
            case (Phase.Intro, 4):
                Say(NpcReethe, SayRetNoFire);
                break;
            case (Phase.Intro, 5):
                Say(NpcReethe, SayRetHear);
                foreach (uint entry in (ReadOnlySpan<uint>)[NpcCaldwell, NpcHallan, NpcSkirmisher, NpcSkirmisher])
                {
                    system.SummonAt(Me, entry, GuestSpawn.X, GuestSpawn.Y, GuestSpawn.Z, 0f, target: null, GuestLifetimeMs); // TIMED_OOC_OR_DEAD, 300000
                }

                _phase = Phase.Guests;
                break;
            case (Phase.Guests, 6):
                Say(NpcCaldwell, SayCalFound);
                break;
            case (Phase.Guests, 7):
                Say(NpcCaldwell, SayCalMercy);
                break;
            case (Phase.Guests, 8):
                if (GetCreature(NpcHallan) is { } hallan)
                {
                    system.SayText(hallan, SayHallGlad);
                    if (GetCreature(NpcReethe) is { } shot)
                    {
                        system.CastSpell(hallan, SpellFakeShot, shot, triggered: false);
                    }
                }

                break;
            case (Phase.Guests, 9):
                if (GetCreature(NpcReethe) is { } reethe)
                {
                    system.SayText(reethe, EmoteRetArrow);
                    system.SayText(reethe, SayRetArrow);
                }

                break;
            case (Phase.Guests, 10):
                Say(NpcCaldwell, SayCalCleanup);
                system.SayText(Me, SayNoDie);
                break;
            case (Phase.Guests, 11):
                StartAttackMe();
                _phase = Phase.Fight;
                break;
            case (Phase.Complete, 12):
                if (GetPlayerForEscort() is { } player)
                {
                    system.RewardGroupEventExplored(player, QuestQuestioning, Me);
                }

                system.SayText(Me, SaySurvive);
                break;
            case (Phase.Complete, 13):
                Say(NpcReethe, SayRetLucky);
                break;
            case (Phase.Complete, 14):
                system.SayText(Me, SayThanks);
                SetRun(true);
                SetEscortPaused(false);
                break;
        }
    }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Feero Ironhand (entry 4484, Ashenvale), the escort of "Supplies to Auberdine" (quest 976): mangos-classic ScriptDev2 <c>npc_feero_ironhand</c>
/// (kalimdor/ashenvale.cpp:591-767, registered :825-829) on his 33 point script_waypoint path. Accepting the quest makes him say his line,
/// take the neutral passive escort faction (10) and walk at a run, restarting at once if he is lost (QuestAccept :753-766). Three ambushes
/// come for him, each out-of-combat despawning 20 s after its fight (TEMPSPAWN_TIMED_OOC_DESPAWN) and attacking him as soon as it is
/// summoned (JustSummoned :732-746): at point 15 four Dark Strand Assassins (3879) 30 yd away in a fan from angle 1.25 down in steps of pi/4, at
/// 21 three Forsaken Scouts (3893), one of whom (the second summoned) calls out to him, 30 yd away from angle 0.95 in steps of pi/3, and at
/// 30 the three elites Balizar (3899), Aligar (3898) and Caedakar (3900) at fixed spots, Balizar yelling as he arrives. The last of a wave
/// to die makes him speak (SummonedCreatureJustDied :709-730) and he yells once when Balizar first hits him (AttackedBy :687-699). At 31 the
/// quest is done for the group. The wave counter resets with the script only while he is not escorting (Reset :647-654).
/// <para>
/// Differences: the ambush points of the fans are the ground height under the point 30 yd away (GetNearPoint's collision search, which can move
/// the point, is not ported); his AttackedBy override does not call the base reaction, as in the source, so a retaliation comes only from the
/// threat the attackers put on him.
/// </para>
/// </summary>
public sealed class FeeroIronhandAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 4484, QuestSuppliesToAuberdine = 976, FactionEscortNeutralPassive = 10;
    public const uint NpcDarkStrandAssassin = 3879, NpcForsakenScout = 3893, NpcAligar = 3898, NpcBalizar = 3899, NpcCaedakar = 3900;
    public const int SayQuestStart = -1000771, SayFirstAmbushStart = -1000772, SayFirstAmbushEnd = -1000773, SaySecondAmbushStart = -1000774,
        SayScoutSecondAmbush = -1000775, SaySecondAmbushEnd = -1000776, SayFinalAmbushStart = -1000777, SayBalizarFinalAmbush = -1000778,
        SayFinalAmbushAttack = -1000779, SayQuestEnd = -1000780;
    public const uint AmbushLifetimeMs = 20_000;

    /// <summary>aSummonPositions (:623-627): the distance and the first angle of the two fans.</summary>
    public static readonly (float Distance, float Angle)[] FanPositions = [(30f, 1.25f), (30f, 0.95f)];

    /// <summary>aEliteSummonPositions (:630-634): Balizar, Aligar and Caedakar.</summary>
    public static readonly (float X, float Y, float Z, float O)[] ElitePositions =
        [(4243.12f, 108.22f, 38.12f, 3.62f), (4240.95f, 114.04f, 38.35f, 3.56f), (4235.78f, 118.09f, 38.08f, 4.12f)];

    private byte _creaturesCount;
    private bool _isAttacked;

    /// <summary>m_uiCreaturesCount: the summons of the current wave still standing (a byte, as there).</summary>
    public byte CreaturesCount => _creaturesCount;

    /// <summary>m_bIsAttacked: Balizar has already hit him once.</summary>
    public bool IsAttacked => _isAttacked;

    /// <summary>QuestAccept_npc_feero_ironhand (:753-766).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestSuppliesToAuberdine)
        {
            return;
        }

        System?.SayText(Me, SayQuestStart, player);

        // TEMPFACTION_RESTORE_RESPAWN | TEMPFACTION_TOGGLE_IMMUNE_TO_NPC: the toggle clears the template's immunity to creatures, JustRespawned puts it back.
        Me.SetFactionTemporary(FactionEscortNeutralPassive, TemporaryFactionFlags.RestoreRespawn);
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        Start(run: true, instantRespawn: true, player: player, questId: questId);
    }

    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _creaturesCount = 0;
            _isAttacked = false;
        }
    }

    protected override void JustRespawned()
    {
        base.JustRespawned();
        if ((Me.Template.UnitFlags & (uint)UnitFlags.ImmuneToNpc) != 0)
        {
            Me.UnitFlags |= UnitFlags.ImmuneToNpc;
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 15:
                System?.SayText(Me, SayFirstAmbushStart);
                for (int i = 0; i < 4; i++)
                {
                    SpawnMob(NpcDarkStrandAssassin, FanPositions[0].Distance, FanPositions[0].Angle - (MathF.PI / 4 * i));
                }

                break;
            case 21:
                System?.SayText(Me, SaySecondAmbushStart);
                for (int i = 0; i < 3; i++)
                {
                    SpawnMob(NpcForsakenScout, FanPositions[1].Distance, FanPositions[1].Angle - (MathF.PI / 3 * i));
                }

                break;
            case 30:
                System?.SayText(Me, SayFinalAmbushStart);
                uint[] elites = [NpcBalizar, NpcAligar, NpcCaedakar];
                for (int i = 0; i < elites.Length; i++)
                {
                    (float x, float y, float z, float o) = ElitePositions[i];
                    Summon(elites[i], x, y, z, o);
                }

                break;
            case 31:
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestSuppliesToAuberdine, Me);
                }

                break;
        }
    }

    /// <summary>AttackedBy (:687-699): he yells only the first time Balizar attacks him; there is no base reaction.</summary>
    public override void OnAttackedBy(Unit attacker)
    {
        if (!_isAttacked && attacker is Creature { Entry: NpcBalizar })
        {
            System?.SayText(Me, SayFinalAmbushAttack);
            _isAttacked = true;
        }
    }

    /// <summary>DoSpawnMob (:701-707): a summon at a point <paramref name="distance"/> yd away at the absolute <paramref name="angle"/>.</summary>
    private void SpawnMob(uint entry, float distance, float angle)
    {
        float x = Me.X + (distance * MathF.Cos(angle));
        float y = Me.Y + (distance * MathF.Sin(angle));
        float z = System?.GroundZ(x, y, Me.Z) ?? Me.Z;
        Summon(entry, x, y, z, 0f);
    }

    /// <summary>SummonCreature(..., TEMPSPAWN_TIMED_OOC_DESPAWN, 20000): JustSummoned runs as it appears.</summary>
    private void Summon(uint entry, float x, float y, float z, float orientation)
    {
        if (System is not { } system || system.Content.FindTemplate(entry) is not { } template)
        {
            return;
        }

        Creature summoned = system.SpawnTemporary(template, x, y, z, orientation, Me);
        system.MarkTimedOutOfCombatDespawn(summoned, AmbushLifetimeMs);
    }

    /// <summary>JustSummoned (:732-746): the scout that is second to arrive and Balizar speak, and every summon attacks him.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Entry == NpcForsakenScout)
        {
            // Of the scouts, only the one that arrives second speaks
            if (_creaturesCount == 1)
            {
                System?.SayText(summoned, SayScoutSecondAmbush, Me);
            }
        }
        else if (summoned.Entry == NpcBalizar)
        {
            System?.SayText(summoned, SayBalizarFinalAmbush);
        }

        _creaturesCount++;
        summoned.AI?.AttackStart(Me);
    }

    /// <summary>SummonedCreatureJustDied (:709-730): the counter drops (as an unsigned byte) and the last of a wave makes him speak.</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        _creaturesCount = unchecked((byte)(_creaturesCount - 1));
        if (_creaturesCount != 0)
        {
            return;
        }

        switch (summoned.Entry)
        {
            case NpcDarkStrandAssassin:
                System?.SayText(Me, SayFirstAmbushEnd);
                break;
            case NpcForsakenScout:
                System?.SayText(Me, SaySecondAmbushEnd);
                break;
            case NpcAligar:
            case NpcBalizar:
            case NpcCaedakar:
                System?.SayText(Me, SayQuestEnd);
                break;
        }
    }
}

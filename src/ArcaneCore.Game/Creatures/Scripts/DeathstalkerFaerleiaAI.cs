using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Deathstalker Faerleia (entry 2058, Pyrewood Village, Silverpine Forest), quest 452 "Pyrewood Ambush": mangos-classic ScriptDev2
/// <c>npc_deathstalker_faerleia</c> (eastern_kingdoms/silverpine_forest.cpp:133-306, registered :315-319). Accepting the quest makes her say
/// her line (-1000553) and start the event (QuestAccept, :290-300; StartEvent, :190-199): her quest flag goes, and ten seconds later the council
/// comes back in four waves, each coming only once the one before is dead: Smithers; Thatcher and Hendricks; Wilhelm, Hartin and Higarth; then
/// Cooper, Brunswick and Lord Mayor Morrison (UpdateAI, :247-288). A summoned councilman walks to a random point within 10 yards of the
/// meeting place (JustSummoned, :216-224); each is a corpse-timed (20 s) summon. When a wave is dead she drinks a potion (3359); after the
/// last she says her closing line (-1000554) and the player and group get the event credit (SummonedCreatureJustDied, :226-245). If she dies
/// the player is sent the quest-failed packet (JustDied, :208-214). Either end gives her quest flag back (FinishEvent, :201-206).
/// The source's SendQuestFailed is only the SMSG_QUESTGIVER_QUEST_FAILED packet (mangos-classic Entities/Player.cpp:13877-13887) and leaves the quest in
/// the log, which is what is sent here too (Player.h:1325 gives reason 0 by default).
/// </summary>
public sealed class DeathstalkerFaerleiaAI(Creature creature) : ScriptedAI(creature), IQuestScriptAI
{
    public const uint Entry = 2058, QuestPyrewoodAmbush = 452, SpellDrinkPotion = 3359;
    public const int SayStart = -1000553, SayCompleted = -1000554;
    public const uint NpcSmithers = 2060, NpcThatcher = 2061, NpcHendricks = 2062, NpcWilhelm = 2063, NpcHartin = 2064, NpcHigarth = 2066,
        NpcCooper = 2065, NpcBrunswick = 2067, NpcMorrison = 2068;
    public const uint CorpseDespawnMs = 20_000, FirstWaveMs = 10_000;

    private static readonly (float X, float Y, float Z, float O)[] s_spawnPoints =
    [
        (-397.45f, 1509.56f, 18.87f, 4.73f), (-398.35f, 1510.75f, 18.87f, 4.76f), (-396.41f, 1511.06f, 18.87f, 4.74f),
    ];

    private static readonly (float X, float Y, float Z) s_moveCentre = (-410.69f, 1498.04f, 19.77f);

    private ObjectGuid _playerGuid;
    private uint _waveMs;
    private int _summonCount, _waveCount;
    private bool _eventStarted;

    public bool EventStarted => _eventStarted;

    public int WaveCount => _waveCount;

    public int AliveCouncilmen => _summonCount;

    protected override void Reset()
    {
    }

    /// <summary>QuestAccept_npc_deathstalker_faerleia (:290-300).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestPyrewoodAmbush)
        {
            return;
        }

        System?.SayText(Me, SayStart, player);
        StartEvent(player);
    }

    private void StartEvent(Player player)
    {
        Me.NpcFlags &= ~(uint)NpcFlags.QuestGiver;
        _playerGuid = player.Guid;
        _eventStarted = true;
        _waveMs = FirstWaveMs;
        _summonCount = 0;
        _waveCount = 0;
    }

    private void FinishEvent()
    {
        _playerGuid = default;
        _eventStarted = false;
        Me.NpcFlags |= (uint)NpcFlags.QuestGiver;
    }

    /// <summary>JustDied (:208-214): Player::SendQuestFailed(quest) is the failed packet with reason 0 (DONT_HAVE_REQ).</summary>
    public override void OnDeath(Unit? killer)
    {
        if (System?.Map.FindPlayer(_playerGuid) is { } player)
        {
            player.Session.Send(WorldOpcode.SmsgQuestgiverQuestFailed, QuestPackets.QuestFailed(QuestPyrewoodAmbush, QuestInvalidReason.DontHaveReq).AsSpan());
        }

        FinishEvent();
    }

    /// <summary>JustSummoned (:216-224): counted, and sent walking to a random point near the meeting place.</summary>
    public override void OnJustSummoned(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        _summonCount++;
        if (System is { } system)
        {
            float angle = system.RandomInt(0, 35_999) * (MathF.PI / 18_000f);
            float distance = 10f * MathF.Sqrt(system.RandomInt(0, 10_000) / 10_000f);
            system.MoveTo(summoned, s_moveCentre.X + (distance * MathF.Cos(angle)), s_moveCentre.Y + (distance * MathF.Sin(angle)), s_moveCentre.Z,
                run: false, finalOrientation: null);
        }
    }

    /// <summary>SummonedCreatureJustDied (:226-245).</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        if (_summonCount > 0)
        {
            _summonCount--;
        }

        if (_summonCount != 0)
        {
            return;
        }

        DoCast(Me, SpellDrinkPotion);
        if (_waveCount == 4)
        {
            System?.SayText(Me, SayCompleted);
            if (System?.Map.FindPlayer(_playerGuid) is { } player)
            {
                System.RewardGroupEventExplored(player, QuestPyrewoodAmbush, Me);
            }

            FinishEvent();
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_eventStarted && _summonCount == 0)
        {
            if (_waveMs < diffMs)
            {
                switch (_waveCount)
                {
                    case 0:
                        Summon(NpcSmithers, 1);
                        _waveMs = 10_000;
                        break;
                    case 1:
                        Summon(NpcThatcher, 2);
                        Summon(NpcHendricks, 1);
                        _waveMs = 10_000;
                        break;
                    case 2:
                        Summon(NpcWilhelm, 1);
                        Summon(NpcHartin, 0);
                        Summon(NpcHigarth, 2);
                        _waveMs = 8_000;
                        break;
                    case 3:
                        Summon(NpcCooper, 1);
                        Summon(NpcBrunswick, 2);
                        Summon(NpcMorrison, 0);
                        break;
                }

                _waveCount++;
            }
            else
            {
                _waveMs -= diffMs;
            }
        }

        UpdateVictim();
    }

    private void Summon(uint entry, int point)
    {
        (float x, float y, float z, float o) = s_spawnPoints[point];
        System?.SummonCorpseTimedDespawn(Me, entry, x, y, z, o, null, CorpseDespawnMs);
    }
}

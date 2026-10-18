using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Captured Arko'narin (entry 11016, Jaedenar, Felwood), "Rescue From Jaedenar" (quest 5203): mangos-classic ScriptDev2
/// <c>npc_captured_arkonarin</c> (kalimdor/felwood.cpp:164-306, registered :502-506) on <see cref="EscortAI"/>. She kneels in her cage. Taking the
/// quest stands her up, gives her the neutral active escort faction (250, not immune to NPCs) until respawn, opens the cage within 5 yd (176306)
/// and walks the sniffed waypoint_path 11016, whose movement scripts (dbscripts_on_creature_movement 1101601-1101609) do the rest of the story:
/// the chest, her transformation, the Legionnaires and the Spirit of Trey (11141). The script itself says her start line at point 1, fights with
/// Mortal Strike and Cleave from point 37, stops at 83 until Trey dies, and at 85 faces the player, gives the group the quest event, turns immune to
/// NPCs and lets go of the quest. In a fight she draws one of three lines four times in five.
/// </summary>
public sealed class CapturedArkonarinAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 11016, QuestRescueJaedenar = 5203, FactionEscortNeutralActive = 250, NpcSpiritTrey = 11141, GoArkonarinCage = 176306;
    public const uint PathId = 11016, SpellMortalStrike = 16856, SpellCleave = 15496, EmoteOneshotTalk = 1;
    public const int SayEscortStart = 6433;
    public const float CageRange = 5f;

    /// <summary>aRandomAggro (:186): broadcast texts.</summary>
    public static readonly int[] RandomAggro = [6801, 6802, 6803];

    private bool _canAttack;
    private uint _mortalStrikeMs, _cleaveMs;

    public bool CanAttack => _canAttack;

    /// <summary>The constructor (:190-194): kneeling.</summary>
    protected override void JustSpawned() => Me.StandState = StandState.Kneel;

    /// <summary>Reset (:203-210).</summary>
    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _canAttack = false;
        }

        _mortalStrikeMs = (uint)(System?.RandomInt(5000, 7000) ?? 5000);
        _cleaveMs = (uint)(System?.RandomInt(1000, 4000) ?? 1000);
    }

    /// <summary>Aggro (:212-216): four in five, one of three lines to the target.</summary>
    protected override void Aggro(Unit target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (System is { } system && system.RandomInt(0, 99) < 80)
        {
            system.SayText(Me, RandomAggro[system.RandomInt(0, RandomAggro.Length - 1)], target);
        }
    }

    /// <summary>SummonedCreatureJustDied (:218-222): the Spirit of Trey's death sends her on.</summary>
    public override void OnSummonedCreatureJustDied(Creature summoned)
    {
        ArgumentNullException.ThrowIfNull(summoned);
        if (summoned.Entry == NpcSpiritTrey)
        {
            SetEscortPaused(false);
        }
    }

    /// <summary>QuestAccept_npc_captured_arkonarin and ReceiveAIEvent (:224-235, :300-306).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestRescueJaedenar)
        {
            return;
        }

        Me.StandState = StandState.Stand;
        Me.SetFactionTemporary(FactionEscortNeutralActive, TemporaryFactionFlags.RestoreRespawn | TemporaryFactionFlags.ToggleImmuneToNpc);
        Start(run: false, player: player, questId: questId, waypointPath: PathId);
        if (System?.Map.FindUpdater<GameObjectMapSystem>() is { } objects && objects.ClosestOfEntry(Me, GoArkonarinCage, CageRange) is { } cage)
        {
            objects.UseByUnit(Me, cage);
        }
    }

    /// <summary>WaypointReached (:237-265).</summary>
    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                if (GetPlayerForEscort() is { } player && System is { } system)
                {
                    system.SayText(Me, SayEscortStart, player);
                    system.PlayEmote(Me, EmoteOneshotTalk);
                }

                break;
            case 37:
                _canAttack = true;
                break;
            case 83:
                SetEscortPaused(true);
                break;
            case 85:
                if (GetPlayerForEscort() is { } escort && System is { } s)
                {
                    s.SetFacingTo(Me, MathF.Atan2(escort.Y - Me.Y, escort.X - Me.X)); // SetFacingToObject
                    s.RewardGroupEventExplored(escort, QuestRescueJaedenar, Me);
                }

                Me.UnitFlags |= UnitFlags.ImmuneToNpc;
                QuestEnd();
                break;
        }
    }

    /// <summary>UpdateEscortAI (:267-292): Mortal Strike every 7-10 s and Cleave every 3-6 s, once she can fight.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim || !_canAttack)
        {
            return;
        }

        if (_mortalStrikeMs < diffMs)
        {
            if (DoCast(victim, SpellMortalStrike) == CreatureCastResult.Ok)
            {
                _mortalStrikeMs = (uint)(System?.RandomInt(7000, 10000) ?? 7000);
            }
        }
        else
        {
            _mortalStrikeMs -= diffMs;
        }

        if (_cleaveMs < diffMs)
        {
            if (DoCast(victim, SpellCleave) == CreatureCastResult.Ok)
            {
                _cleaveMs = (uint)(System?.RandomInt(3000, 6000) ?? 3000);
            }
        }
        else
        {
            _cleaveMs -= diffMs;
        }
    }
}

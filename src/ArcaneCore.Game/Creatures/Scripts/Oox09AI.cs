using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// OOX-09/HL (entry 7806, Hinterlands), "Rescue OOX-09/HL!" (quest 836): mangos-classic ScriptDev2 <c>npc_00x09hl</c>
/// (eastern_kingdoms/hinterlands.cpp). He lies "dead" until the quest is accepted; then he says his start line, stands, takes the escort
/// faction (232, hit by NPCs) and walks. Ambush lines at 27 and 44; on leaving 28 three Marauding Owls (7808) and on leaving 45 three Vile
/// Ambushers (7809) come from a 7 yd circle and walk to him (corpse despawn 25 s). At 65 he gives his end line and the group credit. He
/// does not greet the ambushers; anyone else gets one of two aggro lines. If he dies his ambushers vanish.
/// </summary>
public sealed class Oox09AI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 7806, QuestRescueOox09 = 836, NpcMaraudingOwl = 7808, NpcVileAmbusher = 7809, FactionEscort = 232;
    public const int SayStart = -1000287, SayAggro1 = -1000288, SayAggro2 = -1000289, SayAmbush = -1000290, SayEnd = -1000292;

    private readonly List<Creature> _summons = [];
    private int _summonCount;

    public int SummonCount => _summonCount;

    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _summonCount = 0;
            Me.StandState = StandState.Dead;
        }
    }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestRescueOox09)
        {
            System?.SayText(Me, SayStart, player);
            Me.StandState = StandState.Stand;
            Me.FactionTemplate = FactionEscort;
            Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
            Start(run: false, player: player, questId: questId);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 27:
            case 44:
                System?.SayText(Me, SayAmbush);
                break;
            case 65:
                System?.SayText(Me, SayEnd);
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestRescueOox09, Me);
                }

                break;
        }
    }

    protected override void WaypointStart(uint pointId)
    {
        switch (pointId)
        {
            case 28 when _summonCount < 3:
                Ambush(NpcMaraudingOwl, 147.927444f, -3851.513428f, 130.893f);
                break;
            case 45 when _summonCount < 6:
                Ambush(NpcVileAmbusher, -141.151581f, -4291.213867f, 120.130f);
                break;
        }
    }

    /// <summary>Three of <paramref name="entry"/> at random points within 7 yd of the spot, each walking to OOX-09.</summary>
    public void Ambush(uint entry, float x, float y, float z)
    {
        if (System is not { } system)
        {
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            float angle = system.RandomInt(0, 359) * MathF.PI / 180f, radius = system.RandomInt(0, 700) / 100f;
            if (system.SummonCorpseTimedDespawn(Me, entry, x + (radius * MathF.Cos(angle)), y + (radius * MathF.Sin(angle)), z, 0f, null, 25_000)
                is { } summoned)
            {
                _summons.Add(summoned);
                system.MoveTo(summoned, Me.X, Me.Y, Me.Z, run: false, finalOrientation: null);
            }

            _summonCount++;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (target is Creature { Entry: NpcMaraudingOwl or NpcVileAmbusher })
        {
            return;
        }

        if (System is { } system)
        {
            system.SayText(Me, system.RandomInt(0, 1) == 0 ? SayAggro1 : SayAggro2);
        }
    }

    public override void OnDeath(Unit? killer)
    {
        foreach (Creature summoned in _summons)
        {
            if (summoned.IsAlive)
            {
                System?.ForcedDespawn(summoned, 0);
            }
        }

        _summons.Clear();
        base.OnDeath(killer);
    }
}

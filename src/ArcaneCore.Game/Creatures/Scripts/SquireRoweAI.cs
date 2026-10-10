using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Squire Rowe (entry 17804, Stormwind gates), the signal for Marshal Windsor in quests 6402/6403: mangos-classic ScriptDev2
/// <c>npc_squire_rowe</c> (eastern_kingdoms/stormwind_city.cpp at 3e8597afe7). From his gossip he runs his z2815 script path: he kneels at
/// 3, fires a blue firework at 4 and summons Reginald Windsor, who runs up to the gate. At 7 he says the signal is sent and waits; once
/// Windsor has arrived the intro plays (dismount, his line, he faces the player and welcomes him, now a quest giver), and Rowe walks back.
/// SD2 starts the intro from Windsor's movement inform while Rowe is paused; here Rowe watches for Windsor at his point while paused.
/// </summary>
public sealed class SquireRoweAI(Creature creature) : EscortAI(creature)
{
    public const uint Entry = 17804, NpcWindsor = 12580, SpellBlueFirework = 11540, SpellDismissHorse = 20000;
    public const uint QuestStormwindRendezvous = 6402, QuestTheGreatMasquerade = 6403;
    public const int SaySignalSent = -1000822, SayDismount = -1000823, SayWelcome = -1000824;
    private static readonly (float X, float Y, float Z) s_windsorSpawn = (-9145.68f, 373.79f, 90.64f);
    private static readonly (float X, float Y, float Z) s_windsorMove = (-9050.39f, 443.55f, 93.05f);

    private Creature? _windsor;
    private int _dialogueStep = -1;
    private uint _dialogueMs;

    /// <summary>IsStormwindQuestActive: Windsor is out (until his summon despawns).</summary>
    public bool EventInProgress => _windsor is { IsAlive: true } windsor && System?.Creatures.Contains(windsor) == true;

    public Creature? Windsor => _windsor;

    /// <summary>GossipSelect: Start(true, player, nullptr, true, false).</summary>
    public bool StartFromGossip(Player player) => Start(run: true, instantRespawn: true, player: player);

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 3:
                Me.StandState = StandState.Kneel;
                break;
            case 4:
                DoCast(Me, SpellBlueFirework, triggered: true);
                Me.StandState = StandState.Stand;
                // TEMPSPAWN_CORPSE_DESPAWN; JustSummoned: run to the gate.
                if (System?.SummonAt(Me, NpcWindsor, s_windsorSpawn.X, s_windsorSpawn.Y, s_windsorSpawn.Z, 0f, null, 0) is { } windsor)
                {
                    _windsor = windsor;
                    _dialogueStep = -1;
                    System.MoveTo(windsor, s_windsorMove.X, s_windsorMove.Y, s_windsorMove.Z, run: true, finalOrientation: null);
                }

                break;
            case 7:
                System?.SayText(Me, SaySignalSent);
                SetEscortPaused(true);
                break;
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (_windsor is not { } windsor || System is not { } system)
        {
            return;
        }

        if (_dialogueStep < 0)
        {
            float dx = windsor.X - s_windsorMove.X, dy = windsor.Y - s_windsorMove.Y;
            if (HasEscortState(EscortState.Paused) && (dx * dx) + (dy * dy) <= 1f)
            {
                _dialogueStep = 0; // {NPC_WINDSOR, 0, 3000}: wait
                _dialogueMs = 3000;
            }

            return;
        }

        if (_dialogueStep >= 4)
        {
            return;
        }

        if (_dialogueMs > diffMs)
        {
            _dialogueMs -= diffMs;
            return;
        }

        _dialogueStep++;
        Player? player = GetPlayerForEscort();
        switch (_dialogueStep)
        {
            case 1: // NPC_WINDSOR_MOUNT, 1000
                windsor.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 0);
                system.SetFacingTo(windsor, 1.5636f);
                system.CastSpell(windsor, SpellDismissHorse, windsor, false);
                _dialogueMs = 1000;
                break;
            case 2: // SAY_DISMOUNT by Windsor, 2000
                system.SayText(windsor, SayDismount);
                _dialogueMs = 2000;
                break;
            case 3: // QUEST_STORMWIND_RENDEZVOUS: face the player, 2000
                if (player is not null)
                {
                    system.SetFacingTo(windsor, MathF.Atan2(player.Y - windsor.Y, player.X - windsor.X));
                }

                _dialogueMs = 2000;
                break;
            case 4: // QUEST_THE_GREAT_MASQUERADE: welcome, quest giver, Rowe walks on
                if (player is not null)
                {
                    system.SayText(windsor, SayWelcome, player);
                    windsor.NpcFlags |= (uint)NpcFlags.QuestGiver;
                    SetEscortPaused(false);
                }

                break;
        }
    }
}

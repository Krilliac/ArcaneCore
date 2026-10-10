using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Private Hendel (entry 4966, Dustwallow Marsh), "The Missing Diplomat" part 16 (quest 1324): mangos-classic ScriptDev2
/// <c>npc_private_hendel</c> (kalimdor/dustwallow_marsh.cpp at 3e8597afe7). Accepting turns him and the Theramore Sentries within 40 yards
/// hostile (faction 168) and they attack. Below 20% he surrenders: he evades, the sentries run off and despawn after 4 s, and Tervosh, Jaina
/// and Pained teleport in. Tervosh's waypoint DB script then plays the speech and casts Teleport (7079) on Hendel, which credits the player.
/// Difference: the player stored for credit is the one who accepted (still with the quest incomplete), not the one who dealt the last blow.
/// </summary>
public sealed class PrivateHendelAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint Entry = 4966, QuestMissingDiplomat16 = 1324, FactionHostile = 168, NpcSentry = 5184;
    public const uint NpcJaina = 4968, NpcTervosh = 4967, NpcPained = 4965, SpellTeleportVisual = 12980, SpellTeleport = 7079;
    public const int EmoteSurrender = -1000415;
    private static readonly (uint Entry, float X, float Y, float Z, float O, float Dx, float Dy, float Dz)[] s_outro =
    [
        (NpcTervosh, -2857.604492f, -3354.784912f, 35.369640f, 3.16604f, -2881.546631f, -3346.477539f, 34.143719f),
        (NpcJaina, -2858.120117f, -3358.469971f, 36.086300f, 3.16604f, -2879.697998f, -3347.789063f, 34.772892f),
        (NpcPained, -2857.379883f, -3351.370117f, 34.178001f, 3.16604f, -2879.959961f, -3344.469971f, 34.670502f),
    ];
    private static readonly (float X, float Y, float Z) s_sentryFlee = (-2917.56f, -3329.90f, 30.37f);

    private Player? _accepter;
    private ObjectGuid _creditPlayer;

    public bool Surrendered { get; private set; }

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestMissingDiplomat16 || System is not { } system)
        {
            return;
        }

        _accepter = player;
        Surrendered = false;
        Me.FactionTemplate = FactionHostile;
        Me.InvincibilityHpThreshold = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 20 / 100));
        system.AttackStart(Me, player);
        foreach (Creature sentry in system.CreaturesOfEntryInRange(Me, NpcSentry, 40f).Where(c => c.IsAlive))
        {
            sentry.FactionTemplate = FactionHostile;
            system.AttackStart(sentry, player);
        }
    }

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.Id == SpellTeleport && !_creditPlayer.IsEmpty && System?.Map.FindPlayer(_creditPlayer) is { } player)
        {
            System.RewardGroupEventExplored(player, QuestMissingDiplomat16, Me);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Surrendered && Me.InvincibilityHpThreshold != 0 && Me.Health <= Me.InvincibilityHpThreshold)
        {
            Surrender();
            return;
        }

        base.OnUpdate(diffMs);
    }

    private void Surrender()
    {
        if (System is not { } system)
        {
            return;
        }

        Surrendered = true;
        Me.InvincibilityHpThreshold = 0;
        if (_accepter is { } player) // the quest system only credits it while incomplete
        {
            _creditPlayer = player.Guid;
        }

        system.SayText(Me, EmoteSurrender);
        EnterEvadeMode();
        foreach (Creature sentry in system.CreaturesOfEntryInRange(Me, NpcSentry, 40f).Where(c => c.IsAlive))
        {
            system.EnterEvadeMode(sentry);
            system.MoveTo(sentry, s_sentryFlee.X, s_sentryFlee.Y, s_sentryFlee.Z, run: true, finalOrientation: null);
            system.ForcedDespawn(sentry, 4000);
        }

        foreach ((uint entry, float x, float y, float z, float o, float dx, float dy, float dz) in s_outro)
        {
            // TEMPSPAWN_TIMED_DESPAWN, 3 minutes.
            if (system.SummonAt(Me, entry, x, y, z, o, null, 180_000) is not { } summoned)
            {
                continue;
            }

            system.CastSpell(summoned, SpellTeleportVisual, summoned, triggered: false);
            if (entry == NpcTervosh)
            {
                // His DB script (the waypoint movement) gives the flags back and does the speech.
                summoned.NpcFlags &= ~((uint)NpcFlags.QuestGiver | (uint)NpcFlags.Gossip);
                system.StartEntryWaypointPath(summoned, 0);
            }
            else
            {
                system.MoveTo(summoned, dx, dy, dz, run: false, finalOrientation: null);
            }
        }
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Bartleby (entry 6090, Stormwind), quest 1640 "Beat Bartleby": mangos-classic ScriptDev2 <c>npc_bartleby</c>
/// (eastern_kingdoms/stormwind_city.cpp). Taking the quest makes him hostile (faction 168, restored when the fight stops) and he attacks the
/// player; a hit that would take him under 15% leaves him above it, completes the quest for the player who struck it, and he evades.
/// The combat engine's invincibility threshold stands in for DamageTaken's clamp; the credit goes to his victim at that moment.
/// </summary>
public sealed class BartlebyAI(Creature creature) : CreatureAI(creature), IQuestScriptAI
{
    public const uint Entry = 6090, QuestBeat = 1640, FactionEnemy = 168;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestBeat)
        {
            return;
        }

        Me.FactionTemplate = FactionEnemy; // TEMPFACTION_RESTORE_RESPAWN | TEMPFACTION_RESTORE_COMBAT_STOP
        Me.InvincibilityHpThreshold = Math.Max(1u, (uint)((ulong)Me.MaxHealth * 15 / 100));
        _quester = player.Guid;
        AttackStart(player);
    }

    private ObjectGuid _quester;

    public override void OnRespawn() => Restore();

    public override void OnEvade() => Restore();

    private void Restore()
    {
        Me.FactionTemplate = Me.Template.Faction;
        Me.InvincibilityHpThreshold = 0;
    }

    public override void OnUpdate(uint diffMs)
    {
        // DamageTaken: (health - damage) * 100 / maxHealth < 15 -> clamp, AreaExploredOrEventHappens for a player dealer, EnterEvadeMode.
        if (Me.InvincibilityHpThreshold != 0 && Me.Health <= Me.InvincibilityHpThreshold)
        {
            // The dealer: his player victim, else the player who took the quest.
            if ((Victim as Player ?? System?.Map.FindPlayer(_quester)) is { } player)
            {
                System?.QuestEventHappened(player, QuestBeat);
            }

            _quester = default;
            EnterEvadeMode();
            return;
        }

        UpdateVictim();
    }
}

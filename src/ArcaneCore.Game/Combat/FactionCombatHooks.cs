using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Production <see cref="CombatHooks"/>: a player cannot attack a non-player whose
/// FactionTemplate.dbc row (the <see cref="FactionTemplateCatalog"/> the creature and quest areas
/// already load) is friendly to the player's, by <see cref="FactionTemplateRecord.IsFriendlyTo"/>
/// (explicit enemies deny, explicit friends allow, then the friendly masks; DBCStructure.h as
/// cited on the record, not checked against a vmangos/cmangos checkout).
/// <para>
/// Only <see cref="CanAttack"/> is overridden. <see cref="CombatHooks.IsFriendly"/> is left alone
/// on purpose: spell targeting (friendly AoE, chain heal, dispel polarity) and other consumers
/// route through it, and this change must not alter them. This is not the predicate
/// <see cref="Creatures.FactionCreatureHostility"/> uses (that one is <c>IsHostileTo</c> plus the
/// contested-guard flag, from the creature's side); the two are cross-checked for the catalog
/// pairs in tests, not proven equivalent. A neutral template is neither friendly nor hostile and
/// stays attackable.
/// </para>
/// <para>
/// Not modelled: player reputation and at-war state, and contested-guard state. Templates missing
/// from the catalog (including template 0) are not friendly, i.e. the permissive base rule
/// applies. Player versus player and every non-faction rule of <see cref="CombatHooks.CanAttack"/>
/// are inherited unchanged. A world with no loaded catalog does not register these hooks at all
/// (see WorldCombatHooksFeature).
/// </para>
/// </summary>
public sealed class FactionCombatHooks(FactionTemplateCatalog factions) : CombatHooks
{
    public FactionTemplateCatalog Factions { get; } = factions ?? throw new ArgumentNullException(nameof(factions));

    public override bool CanAttack(Unit attacker, Unit victim)
    {
        if (!base.CanAttack(attacker, victim))
        {
            return false;
        }

        if (attacker is Player != victim is Player)
        {
            Unit npc = attacker is Player ? victim : attacker;
            Unit player = attacker is Player ? attacker : victim;
            if (Factions.Find(npc.FactionTemplate) is { } npcTemplate && Factions.Find(player.FactionTemplate) is { } playerTemplate
                && npcTemplate.IsFriendlyTo(playerTemplate))
            {
                return false;
            }
        }

        return true;
    }
}

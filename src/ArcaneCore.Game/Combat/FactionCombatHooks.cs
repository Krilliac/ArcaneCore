using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Production <see cref="CombatHooks"/>: player versus non-player friendliness comes from
/// FactionTemplate.dbc rows (the <see cref="FactionTemplateCatalog"/> the creature and quest
/// areas already load), so a player cannot attack an NPC whose template is friendly to the
/// player's. Semantics are the same as <see cref="Creatures.FactionCreatureHostility"/>: the
/// non-player's template is evaluated against the player's with
/// <see cref="FactionTemplateRecord.IsFriendlyTo"/> (explicit enemies deny, explicit friends
/// allow, then the friendly masks; vmangos DBCStructure.h, as cited on the record). A neutral NPC
/// is neither friendly nor hostile and stays attackable.
/// <para>
/// Not modelled (same limits as the creature hostility): player reputation and at-war state, and
/// contested-guard state. Templates missing from the catalog (including template 0) are not
/// friendly, i.e. the permissive base rule applies. Player versus player and every non-faction
/// rule of <see cref="CombatHooks.CanAttack"/> are inherited unchanged. A world with no loaded
/// catalog does not register these hooks at all (see WorldCombatHooksFeature).
/// </para>
/// </summary>
public sealed class FactionCombatHooks(FactionTemplateCatalog factions) : CombatHooks
{
    public FactionTemplateCatalog Factions { get; } = factions ?? throw new ArgumentNullException(nameof(factions));

    public override bool IsFriendly(Unit a, Unit b)
    {
        if (a is Player != b is Player)
        {
            Unit npc = a is Player ? b : a;
            Unit player = a is Player ? a : b;
            if (Factions.Find(npc.FactionTemplate) is { } npcTemplate && Factions.Find(player.FactionTemplate) is { } playerTemplate)
            {
                return npcTemplate.IsFriendlyTo(playerTemplate);
            }
        }

        return base.IsFriendly(a, b);
    }
}

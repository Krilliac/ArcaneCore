using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// The Naxxramas clean-up spells a boss casts on itself as it evades: Despawn Crypt Guards (29379, Anub'Rekhan), Despawn Boss Adds (30134,
/// Grobbulus) and Despawn Summons (30228, Noth the Plaguebringer). Each is a SCRIPT_EFFECT over the area around the caster (implicit targets
/// 22/7) whose spell_script_target rows name the adds; the script effect despawns every creature it hits at once.
/// mangos-classic ScriptDevAI naxxramas boss_anubrekhan.cpp DespawnCryptGuards, boss_grobbulus.cpp DespawnBossAdds and boss_noth.cpp
/// DespawnSummonsNoth (each <c>Creature::ForcedDespawn()</c> on the effect's unit target), cast from the bosses' EnterEvadeMode. vmangos has no
/// script for these ids: its boss AIs despawn their summons directly. Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(DespawnCryptGuards, DespawnBossAdds, DespawnSummons)]
public sealed class NaxxramasDespawnSummonsScript : ISpellScript
{
    public const uint DespawnCryptGuards = 29379, DespawnBossAdds = 30134, DespawnSummons = 30228;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect == SpellEffectName.ScriptEffect && context.Target is Creature creature
            && IsListedAdd(context.Spell.Id, creature.Entry) && creature.System is { } creatures)
            creatures.ForcedDespawn(creature, 0);
    }

    /// <summary>
    /// The adds the ClassicDB spell_script_target rows give each spell (29379: Crypt Guard 16573, Corpse Scarab 16698; 30134: Grobbulus Cloud
    /// 16363, Fallout Slime 16290; 30228: Plagued Warrior/Champion/Guardian/Construct 16981-16984). The selector already keeps to these rows; the
    /// check keeps a world without them (an area fill of every living unit) from despawning the boss's neighbours.
    /// </summary>
    internal static bool IsListedAdd(uint spellId, uint entry) => spellId switch
    {
        DespawnCryptGuards => entry is 16573 or 16698,
        DespawnBossAdds => entry is 16363 or 16290,
        DespawnSummons => entry is >= 16981 and <= 16984,
        _ => false,
    };
}

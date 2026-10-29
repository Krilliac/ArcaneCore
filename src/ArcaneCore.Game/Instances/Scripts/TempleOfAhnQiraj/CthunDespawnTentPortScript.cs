using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// Despawn Tent Port (26404), cast by a dying Giant Claw Tentacle (15728): its DUMMY, aimed at the nearest tentacle portal through the ClassicDB
/// spell_script_target rows (Tentacle Portal 15904, Giant Tentacle Portal 15910), despawns that portal at once. mangos-classic ScriptDevAI
/// temple_of_ahnqiraj/boss_cthun.cpp DespawnTentPort (Creature::ForcedDespawn on the unit target), cast from npc_giant_claw_tentacleAI::JustDied.
/// The entry check keeps a world without the rows (the selector then finds no unit) and any other target out of it. Reimplemented, no GPL source
/// copied.
/// </summary>
[SpellScript(DespawnTentPort)]
public sealed class CthunDespawnTentPortScript : ISpellScript
{
    public const uint DespawnTentPort = 26404, TentaclePortal = 15904, GiantTentaclePortal = 15910;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature { Entry: TentaclePortal or GiantTentaclePortal } portal && portal.System is { } creatures)
            creatures.ForcedDespawn(portal, 0);
    }
}

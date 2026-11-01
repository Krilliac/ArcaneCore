using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.BlackwingLair;

/// <summary>
/// The four Random Aggro spells of the Blackrock raids' creature spell lists (22913, 23878, 28388, 30215), siblings of Random Aggro 25680 whose
/// handler is vmangos SpellEffects.cpp:380-402: the casting creature picks a random attacker from its threat list, adds 100 threat on it and
/// attacks it. The spell lists cast them on the creature itself (vmangos migration 20250802133158 creature_spells rows 156210, 161540, 170550, castTarget 1),
/// while the DBC targets are every enemy in 100 yd (A 22 / B 15), so the DUMMY effect would run once per enemy: one cast picks one target, the
/// first effect execution does the pick and the rest are ignored. Reimplemented from the behaviour, no GPL source copied; the pick itself is
/// <see cref="RandomAggroScript"/>.
/// </summary>
[SpellScript(22913, 23878, 28388, 30215)]
public sealed class RandomAggroSpellScripts : ISpellScript
{
    private readonly ConditionalWeakTable<SpellCast, object> _picked = new();
    private readonly RandomAggroScript _pick = new();

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || _picked.TryGetValue(context.Cast, out _))
        {
            return;
        }

        _picked.Add(context.Cast, new object());
        _pick.OnEffectExecute(context);
    }
}

/// <summary>
/// Enraged Felguard Spawn 22393: the Enraged Felguard (14101) casts it on its Demon Portal (14081) as it spawns (ClassicDB EventAI row 1410101;
/// DUMMY at TARGET_SCRIPT 38 with spell_script_target (22393, 1, 14081)). The portal despawns on being hit by it (vmangos migration 20230106131806 creature_ai_events
/// 1408102, 'Despawn on Hit By Spell Enraged Felguard Spawn'). The entry check keeps a world without the target row and any other unit out of it.
/// </summary>
[SpellScript(EnragedFelguardSpawn)]
public sealed class EnragedFelguardSpawnScript : ISpellScript
{
    public const uint EnragedFelguardSpawn = 22393, DemonPortal = 14081;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Target is Creature { Entry: DemonPortal } portal && portal.System is { } creatures)
        {
            creatures.ForcedDespawn(portal, 0);
        }
    }
}

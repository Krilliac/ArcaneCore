using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

// SCRIPT_EFFECT spells of the Temple of Ahn'Qiraj that vmangos leaves without a spell script but mangos-classic (cmangos) implements as
// ScriptDevAI SpellScript classes (src/game/AI/ScriptDevAI/scripts/kalimdor/temple_of_ahnqiraj/boss_viscidus.cpp, boss_cthun.cpp,
// boss_twinemperors.cpp). The behaviour is re-expressed here, not copied. State: every script is a stateless singleton; each hook runs on the
// world thread of the spell's map, and the Creature/AI members it reads belong to that same map.

/// <summary>
/// Summon Globs of Viscidus 25885 (cmangos boss_viscidus.cpp ViscidusSummonGlobs, whose caller vmangos SpellEffects.cpp:1225 is the Viscidus Explode
/// dummy): Viscidus casts one of the twenty Summon Glob spells 25865 to 25884 on himself for every five percent of health he still has, each of them
/// summoning a Glob of Viscidus (15667). The count is the one <see cref="ViscidusAI"/> uses when he shatters, capped at the twenty spells that exist
/// (the reference loop runs one past the table). The globs register themselves with the AI through its summon hook.
/// </summary>
[SpellScript(25885)]
public sealed class ViscidusSummonGlobsScript : ISpellScript
{
    /// <summary>The first of the twenty Summon Glob of Viscidus spells.</summary>
    public const uint FirstGlobSpell = 25865;

    /// <summary>How many Summon Glob spells exist.</summary>
    public const uint GlobSpellCount = 20;

    /// <summary>Viscidus's creature entry.</summary>
    public const uint ViscidusEntry = 15299;

    /// <summary>The number of globs a boss with <paramref name="health"/> of <paramref name="maxHealth"/> sheds: one per full five percent, at most twenty.</summary>
    public static uint GlobCount(uint health, uint maxHealth)
        => maxHealth == 0 ? 0u : Math.Min(GlobSpellCount, (uint)((ulong)health * GlobSpellCount / maxHealth));

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature { Entry: ViscidusEntry } viscidus)
        {
            return;
        }

        uint count = GlobCount(viscidus.Health, viscidus.MaxHealth);
        for (uint i = 0; i < count; i++)
        {
            GadgetCasts.Cast(context, viscidus, viscidus, FirstGlobSpell + i);
        }
    }
}

/// <summary>
/// Despawn Globs 26608 (cmangos boss_viscidus.cpp ViscidusDespawnAdds; vmangos casts it from boss_viscidusAI::JustReachedHome, boss_viscidus.cpp:258):
/// every Glob of Viscidus (15667) the area effect hits is despawned at once. Other creatures in the area are left alone.
/// </summary>
[SpellScript(26608)]
public sealed class ViscidusDespawnGlobsScript : ISpellScript
{
    /// <summary>The Glob of Viscidus creature entry.</summary>
    public const uint GlobEntry = 15667;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature { Entry: GlobEntry } glob)
        {
            WorldDummyCasts.Despawn(glob, 0);
        }
    }
}

/// <summary>
/// Summon Toxic Slime 26584 (cmangos boss_viscidus.cpp SummonToxicSlime): the player it hits casts Summon Toxic Slime 26577 on itself, which puts
/// the poison cloud creature at the player's feet.
/// </summary>
[SpellScript(26584)]
public sealed class ViscidusToxicSlimeScript : ISpellScript
{
    public const uint SummonSlime = 26577;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Target, context.Target, SummonSlime);
    }
}

/// <summary>
/// The four periodic C'Thun tentacle summons (cmangos boss_cthun.cpp SummonCThunTentacles): Summon Giant Hook Tentacles 26217, Summon Mouth Tentacles
/// 26237, Summon Hook Tentacles 26398 and Summon Giant Eye Tentacles 26767 are the ticks of 26213, 26236, 26397 and 26766; the unit each tick hits
/// casts the matching summon spell on itself: 26216 (Giant Claw Tentacle 15728), 26332 (Mouth Tentacle), 26140 (Claw Tentacle 15725) and 26768 (Giant
/// Eye Tentacle 15334). <see cref="CthunBodyAI"/> places its tentacles directly, so only a cast of the spell itself reaches this script.
/// </summary>
[SpellScript(26217, 26237, 26398, 26767)]
public sealed class CthunTentacleSummonScript : ISpellScript
{
    public static uint SummonFor(uint spellId) => spellId switch
    {
        26217 => 26216,
        26237 => 26332,
        26398 => 26140,
        26767 => 26768,
        _ => 0,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint summon = SummonFor(context.Spell.Id);
        if (summon != 0)
        {
            GadgetCasts.Cast(context, context.Target, context.Target, summon);
        }
    }
}

/// <summary>
/// Twin Teleport 799 (cmangos boss_twinemperors.cpp boss_veklorAI::SpellHit and TeleportTwin): when Vek'lor is hit by his own Twin Teleport the
/// brothers trade places. <see cref="VeklorAI.TeleportPair"/> is that exchange (threat reset, Twin Teleport Stun 800 and the visual 26638 on each, the
/// pair then idles before attacking); Vek'lor's own timer calls it directly, so a cast of 799 is the same teleport started by the spell. Spell 799
/// is also used as a plain visual elsewhere (area trigger scripts): a target that is not Vek'lor with the encounter's AI is ignored.
/// </summary>
[SpellScript(799)]
public sealed class TwinTeleportScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature { AI: VeklorAI veklor })
        {
            veklor.TeleportPair();
        }
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

/// <summary>
/// Pyrewood Transformation 7671 (vmangos SpellEffects.cpp:666-712): the Pyrewood villagers of Silverpine turn into their Moonrage worgen form
/// and back (UpdateEntry to the paired entry). Other creatures are left alone.
/// </summary>
[SpellScript(7671)]
public sealed class PyrewoodTransformationScript : ISpellScript
{
    private static readonly Dictionary<uint, uint> s_pairs = new()
    {
        [1891] = 1892, [1892] = 1891, // Pyrewood / Moonrage Watcher
        [1893] = 1894, [1894] = 1893, // Moonrage / Pyrewood Sentry
        [1895] = 1896, [1896] = 1895, // Pyrewood / Moonrage Elder
        [3528] = 3529, [3529] = 3528, // Pyrewood / Moonrage Armorer
        [3530] = 3531, [3531] = 3530, // Pyrewood / Moonrage Tailor
        [3532] = 3533, [3533] = 3532, // Pyrewood / Moonrage Leatherworker
    };

    public static uint PairOf(uint entry) => s_pairs.GetValueOrDefault(entry);

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature villager && villager.System is { } creatures && PairOf(villager.Template.Entry) is var other and not 0)
        {
            creatures.UpdateEntry(villager, other);
        }
    }
}

/// <summary>
/// Symbol of Life 8593 (vmangos SpellEffects.cpp:715-722, SetDeathState(JUST_ALIVED)): a dead creature target comes back to life. The paladin
/// quest NPCs Henze and Narm Faulk only look dead; their scripts stand them up on the spell hit (<see cref="Creatures.Scripts.FaulkAI"/>).
/// </summary>
[SpellScript(8593)]
public sealed class SymbolOfLifeScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature { IsAlive: false } dead && dead.System is { } creatures)
        {
            creatures.ForceRespawn(dead);
        }
    }
}

/// <summary>
/// Coarse Dynamite 9009 (vmangos SpellEffects.cpp:358-370, used by npc 1073): the caster throws one of the three Coarse Dynamite blasts
/// (9002-9004, each landing at its spell_target_position). vmangos casts it a tick later and untriggered; here it is cast at once, triggered,
/// so the throw does not cut the dummy cast short.
/// </summary>
[SpellScript(9009)]
public sealed class CoarseDynamiteScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint spell = 9002u + (uint)GadgetCasts.Roll(context, 3);
        GadgetCasts.Cast(context, context.Caster, context.Caster, spell);
    }
}

/// <summary>Ras Frostwhisper Visual Dummy 17190 (vmangos SpellEffects.cpp:1417-1425): Ras takes his human form (17186) and full health.</summary>
[SpellScript(17190)]
public sealed class RasFrostwhisperVisualScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit ras = context.Target;
        GadgetCasts.Cast(context, ras, ras, 17186);
        ras.Health = ras.MaxHealth;
    }
}

/// <summary>
/// Merging Oozes 16032 (vmangos SpellEffects.cpp:1426-1437): two Un'Goro oozes merge, a Gargantuan Ooze (9621) appears at the caster for up to
/// 7 minutes and both oozes despawn.
/// </summary>
[SpellScript(16032)]
public sealed class MergingOozesScript : ISpellScript
{
    public const uint GargantuanOoze = 9621;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Creature caster || context.Target is not Creature other || ReferenceEquals(caster, other)
            || caster.System is not { } creatures || creatures.Content.FindTemplate(GargantuanOoze) is not { } template)
        {
            return;
        }

        Creature ooze = creatures.SpawnTemporary(template, caster.X, caster.Y, caster.Z, caster.Orientation);
        creatures.ForcedDespawn(ooze, 420_000);
        creatures.ForcedDespawn(caster, 0);
        creatures.ForcedDespawn(other, 0);
    }
}

/// <summary>Piccolo of the Flaming Fire 17512 (vmangos SpellEffects.cpp:3711-3719): a player target starts dancing (EMOTE_STATE_DANCE).</summary>
[SpellScript(17512)]
public sealed class PiccoloOfTheFlamingFireScript : ISpellScript
{
    public const uint EmoteStateDance = 10;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Player dancer)
        {
            dancer.SetUInt32(UpdateFields.UnitNpcEmotestate, EmoteStateDance);
        }
    }
}

/// <summary>
/// Uther's Tribute 24194 / Grom's Tribute 24195 (vmangos SpellEffects.cpp:3754-3800): the player's race picks the tribute buff, Alliance at
/// Uther's tomb (human 24105, dwarf 24107, night elf 24108, gnome 24106) and Horde at Grom's monument (orc 24104, undead 24103, tauren 24102,
/// troll 24101); other races get nothing.
/// </summary>
[SpellScript(24194, 24195)]
public sealed class TributeScript : ISpellScript
{
    public static uint BuffFor(uint spellId, Race race) => (spellId, race) switch
    {
        (24194, Race.Human) => 24105,
        (24194, Race.Dwarf) => 24107,
        (24194, Race.NightElf) => 24108,
        (24194, Race.Gnome) => 24106,
        (24195, Race.Orc) => 24104,
        (24195, Race.Undead) => 24103,
        (24195, Race.Tauren) => 24102,
        (24195, Race.Troll) => 24101,
        _ => 0,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is Player player && BuffFor(context.Spell.Id, player.Race) is var buff and not 0)
        {
            GadgetCasts.Cast(context, player, player, buff);
        }
    }
}

/// <summary>
/// Darkmoon Faire cannon: Fire Cannon 24731 (vmangos SpellEffects.cpp:3947-3954) makes the cannon cast Magic Wings 24742 on the player who fired
/// it; Magic Wings' script effect (:3968-3974) frees the player from the cannon's root (24754).
/// </summary>
[SpellScript(24731, 24742)]
public sealed class DarkmoonCannonScript : ISpellScript
{
    public const uint FireCannon = 24731, MagicWings = 24742, CannonRoot = 24754;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spell.Id == FireCannon)
        {
            if (context.Caster is Player shooter && !ReferenceEquals(context.Target, shooter))
            {
                GadgetCasts.Cast(context, context.Target, shooter, MagicWings);
            }
        }
        else
        {
            context.System.RemoveAuras(context.Target, CannonRoot);
        }
    }
}

/// <summary>Bag of Candies 26678 (vmangos SpellEffects.cpp:4364-4372): one of the eight Hallow's End candy spells (26668, 26670-26676) at random.</summary>
[SpellScript(26678)]
public sealed class BagOfCandiesScript : ISpellScript
{
    private static readonly uint[] s_candies = [26668, 26670, 26671, 26672, 26673, 26674, 26675, 26676];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Caster, context.Caster, s_candies[GadgetCasts.Roll(context, s_candies.Length)]);
    }
}

/// <summary>
/// Love is in the Air: Give Friendship Bracelet 26899 (vmangos SpellEffects.cpp:1358-1367) mends a Heartbroken (26898) target, who makes a bracelet
/// (26921) while the giver gets The Power of Friendship (26664); Remove Amorous 26879 (:1412-1416) ends Amorous (26869); the Silver Shafted Arrow
/// 27662 (:1368-1376) summons Peddlefeet (27570) for a player with no companion out.
/// </summary>
[SpellScript(26899, 26879, 27662)]
public sealed class LoveIsInTheAirScript : ISpellScript
{
    public const uint Heartbroken = 26898, CreateBracelet = 26921, PowerOfFriendship = 26664, Amorous = 26869, SummonPeddlefeet = 27570;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit target = context.Target;
        switch (context.Spell.Id)
        {
            case 26899:
                if (context.System.HasAura(target, Heartbroken))
                {
                    context.System.RemoveAuras(target, Heartbroken);
                    GadgetCasts.Cast(context, target, target, CreateBracelet);
                    GadgetCasts.Cast(context, context.Caster, context.Caster, PowerOfFriendship);
                }

                break;
            case 26879:
                context.System.RemoveAuras(target, Amorous);
                break;
            default:
                if (target is Player player && player.Map?.FindUpdater<Pets.PetMapSystem>()?.MiniPetOf(player) is null)
                {
                    GadgetCasts.Cast(context, player, player, SummonPeddlefeet);
                }

                break;
        }
    }
}

/// <summary>
/// EPL PvP "A Game of Towers": Tower Capture (DND) 31929 makes its player target cast the Tower Kill Credit 32061 (vmangos SpellEffects.cpp:4429-4434),
/// and 32061 (:413-420) gives the casting player kill credit for the tower's credit creature.
/// </summary>
[SpellScript(31929, 32061)]
public sealed class TowerCaptureScript : ISpellScript
{
    public const uint TowerKillCredit = 32061;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Spell.Id == 31929)
        {
            if (context.Target is Player capturer)
            {
                GadgetCasts.Cast(context, capturer, capturer, TowerKillCredit);
            }
        }
        else if (context.Caster is Player player && context.Target is Creature credit)
        {
            credit.System?.KilledMonsterCredit(player, credit.Template.Entry, credit.Guid);
        }
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic;

internal static class WorldDummyHelpers
{
    public static float Dist(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    public static GameObject? NearestObject(WorldObject from, uint entry, float range)
        => from.Map?.FindUpdater<GameObjectMapSystem>()?.GameObjects
            .Where(g => g.Entry == entry && Dist(from, g) <= range).OrderBy(g => Dist(from, g)).FirstOrDefault();
}

/// <summary>
/// Random Aggro 25680 (vmangos SpellEffects.cpp:380-402): the casting creature (or, for a guardian, its creature owner's threat list) picks a
/// random attacker, adds 100 threat on it and attacks it.
/// </summary>
[SpellScript(25680)]
public sealed class RandomAggroScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Caster is not Creature caster || caster.System is not { } creatures)
        {
            return;
        }

        Creature pool = caster;
        if (caster.OwnerGuid != default && caster.Map?.FindObject(caster.OwnerGuid) is Creature owner)
        {
            pool = owner;
        }

        if (!pool.Combat.HasThreatList)
        {
            return;
        }

        Unit[] candidates = [.. pool.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive)];
        if (candidates.Length == 0)
        {
            return;
        }

        Unit target = candidates[GadgetCasts.Roll(context, candidates.Length)];
        caster.Combat.Threat.AddThreat(target, 100f);
        creatures.AttackStart(caster, target);
    }
}

/// <summary>
/// Gordunni Trap 19395 (vmangos SpellEffects.cpp:475-484): the Gordunni trap object makes its victim cast 19394 (Gordunni Trap ogres) or 11756
/// (Gordunni Cobalt) at random, then deactivates.
/// </summary>
[SpellScript(19395)]
public sealed class GordunniTrapScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.CasterGameObject is not { } trap)
        {
            return;
        }

        GadgetCasts.Cast(context, context.Target, context.Target, GadgetCasts.Roll(context, 2) == 0 ? 19394u : 11756u);
        trap.LootState = GameObjectLootState.JustDeactivated;
    }
}

/// <summary>
/// Crystal Prison Dummy DND 23019 (vmangos SpellEffects.cpp:1001-1037): a living non-pet creature is sealed in a Crystal Prison (179644,
/// 10 minutes) at its position and dies.
/// </summary>
[SpellScript(23019)]
public sealed class CrystalPrisonScript : ISpellScript
{
    public const uint CrystalPrisonObject = 179644;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature { IsAlive: true } victim || victim.Summon is { Kind: Pets.SummonKind.Pet } || victim.Map is not { } map)
        {
            return;
        }

        map.FindUpdater<GameObjectMapSystem>()?.Summon(CrystalPrisonObject, victim.X, victim.Y, victim.Z, victim.Orientation, 600);
        map.FindUpdater<MapCombat>()?.Kill(context.Caster, victim);
    }
}

/// <summary>Forgiveness 28697 (vmangos SpellEffects.cpp:1329-1345, SM Ashbringer event): the caster kills the living target.</summary>
[SpellScript(28697)]
public sealed class ForgivenessScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target.IsAlive && context.Caster.Map is { } map)
        {
            map.FindUpdater<MapCombat>()?.Kill(context.Caster, context.Target);
        }
    }
}

/// <summary>
/// Attract Jubjub 23845 (vmangos SpellEffects.cpp:1394-1403): Jubjub walks to the Dark Iron Ale Mug (165578) within 3 yd of the caster.
/// Dark Iron Drunk Mug 14813 (:1377-1393): a target not already Drinking (14823) walks to the mug object that cast it.
/// </summary>
[SpellScript(23845, 14813)]
public sealed class AleMugAttractScript : ISpellScript
{
    public const uint AleMug = 165578, Drinking = 14823;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Creature walker)
        {
            return;
        }

        GameObject? mug;
        if (context.Spell.Id == 23845)
        {
            mug = WorldDummyHelpers.NearestObject(context.Caster, AleMug, 3f);
        }
        else
        {
            if (context.System.HasAura(walker, Drinking))
            {
                return;
            }

            mug = context.CasterGameObject;
        }

        if (mug is not null)
        {
            walker.Motion.MovePoint(1, mug.X, mug.Y, mug.Z, run: false);
        }
    }
}

/// <summary>Silithus Flag Click (DND) 29518 (vmangos SpellEffects.cpp:1404-1411): the clicker takes the Silithyst flag aura 29519.</summary>
[SpellScript(29518)]
public sealed class SilithusFlagClickScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GadgetCasts.Cast(context, context.Target, context.Target, 29519);
    }
}

/// <summary>
/// Teleport to Razelikh (GROUP) 27686 (vmangos SpellEffects.cpp:4385-4400): every group member of the player target within 20 yd of the
/// caster casts Teleport to Razelikh 13142 on themselves.
/// </summary>
[SpellScript(27686)]
public sealed class RazelikhGroupTeleportScript : ISpellScript
{
    public const uint TeleportToRazelikh = 13142;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Player player || player.Map is not { } map)
        {
            return;
        }

        foreach (var guid in context.System.Groups.GetGroupMembers(player, raid: true))
        {
            if (map.FindPlayer(guid) is { } member && WorldDummyHelpers.Dist(member, context.Caster) <= 20f)
            {
                GadgetCasts.Cast(context, member, member, TeleportToRazelikh);
            }
        }
    }
}

/// <summary>
/// Love is in the Air: city guards (Valentine (Guards) 26923) and citizens (26663, 27541, 27547-27550) answer a player (vmangos
/// SpellEffects.cpp:4078-4193). One time in six the player is Heartbroken (26898); otherwise Adored (26680) and a faction pledge/gift, Adoration
/// for the first one and Friendship once already Adored, then Amorous is removed (26879). Guards need a Love Token (21815) and ignore the
/// Heartbroken.
/// </summary>
[SpellScript(26923, 26663, 27541, 27547, 27548, 27549, 27550)]
public sealed class ValentineAnswerScript : ISpellScript
{
    public const uint Heartbroken = 26898, Adored = 26680, LoveToken = 21815, RemoveAmorous = 26879;

    private static readonly Dictionary<uint, (uint Friendship, uint Adoration)> s_guards = new()
    {
        [11] = (27242, 27510), [85] = (27247, 27507), [57] = (27244, 27506), [68] = (27246, 27515), [71] = (27246, 27515),
        [79] = (27245, 27504), [105] = (27248, 27513),
    };

    private static readonly Dictionary<uint, (uint Friendship, uint Adoration)> s_citizens = new()
    {
        [12] = (27525, 27509), [29] = (27523, 27505), [55] = (27520, 27503), [57] = (27520, 27503), [68] = (27529, 27512), [80] = (27519, 26901),
        [83] = (27523, 27505), [85] = (27523, 27505), [104] = (27524, 27511), [105] = (27524, 27511), [123] = (27525, 27509),
        [126] = (27523, 27505), [412] = (27524, 27511), [875] = (27520, 27503), [876] = (27523, 27505), [1215] = (27523, 27505),
    };

    public static (uint Friendship, uint Adoration)? GiftsFor(uint spellId, uint factionTemplate)
        => (spellId == 26923 ? s_guards : s_citizens).TryGetValue(factionTemplate, out var gifts) ? gifts : null;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is not Player player)
        {
            return;
        }

        if (context.Spell.Id == 26923
            && (context.System.HasAura(player, Heartbroken) || player.Inventory.GetItemCount(LoveToken) < 1))
        {
            return;
        }

        if (GiftsFor(context.Spell.Id, context.Caster.FactionTemplate) is not { } gifts)
        {
            return;
        }

        if (GadgetCasts.Roll(context, 6) == 0)
        {
            GadgetCasts.Cast(context, player, player, Heartbroken);
            return;
        }

        uint gift = context.System.HasAura(player, Adored) ? gifts.Friendship : gifts.Adoration;
        GadgetCasts.Cast(context, player, player, Adored);
        GadgetCasts.Cast(context, player, player, gift);
        GadgetCasts.Cast(context, player, player, RemoveAmorous);
    }
}

/// <summary>
/// Love is in the Air Test 27654 / Amorous Timer, Standard Test 26870 (vmangos SpellEffects.cpp:4194-4205): a creature target puts on
/// Love is in the Air (27741) or Amorous (26869) at once.
/// </summary>
[SpellScript(27654, 26870)]
public sealed class AmorousTestScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Target is Creature npc)
        {
            GadgetCasts.Cast(context, npc, npc, context.Spell.Id == 26870 ? 26869u : 27741u);
        }
    }
}

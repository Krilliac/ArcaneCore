using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Generic.Researched;

// Naxxramas spell scripts that vmangos leaves to the boss AI but cmangos (mangos-classic) carries as SpellScripts. The
// ArcaneCore Naxxramas AI follows vmangos and does not cast these spells, so the scripts only run when something does
// cast them (GM, DB script, a future AI). The behaviour is re-expressed from the cmangos scripts cited on each class.

/// <summary>
/// Web Wrap 28673 (cmangos boss_maexxna.cpp WebWrapMaexxna, MAX_PLAYERS_WEB_WRAP = 3): up to three attacking players other
/// than the tank, in random order, each cast one distinct, randomly chosen message spell from 29280, 29281, 29282, 29283,
/// 29285, 29287 on themselves.
/// </summary>
[SpellScript(28673)]
public sealed class WebWrapMaexxnaScript : ISpellScript
{
    public const int MaxPlayers = 3;

    public static IReadOnlyList<uint> MessageSpells { get; } = [29280, 29281, 29282, 29283, 29285, 29287];

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit caster = context.Caster;
        // cmangos SelectAttackingTargets(ATTACKING_TARGET_ALL_SUITABLE, 1, nullptr, SELECT_FLAG_PLAYER | SELECT_FLAG_SKIP_TANK):
        // the threat list from position 1, players only, never the current victim.
        var pool = new List<Player>();
        IReadOnlyList<Combat.ThreatEntry> entries = caster.Combat.Threat.Entries;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].Target is Player { IsAlive: true } player && !ReferenceEquals(player, caster.Combat.Victim))
            {
                pool.Add(player);
            }
        }

        var spells = new List<uint>(MessageSpells);
        for (int picked = 0; picked < MaxPlayers && pool.Count > 0; picked++)
        {
            Player target = pool[GadgetCasts.Roll(context, pool.Count)];
            pool.Remove(target);
            uint spell = spells[GadgetCasts.Roll(context, spells.Count)];
            spells.Remove(spell);
            GadgetCasts.Cast(context, target, target, spell);
        }
    }
}

/// <summary>
/// Web Wrap 29280, 29281, 29282, 29283, 29285, 29287 (cmangos boss_maexxna.cpp WebWrapMessage): the wrapped player casts the
/// pull back at the caster, chosen by the centre distance between them: up to 20 yd 28618, up to 30 yd 28619, up to 40 yd 28620,
/// farther 28621.
/// </summary>
[SpellScript(29280, 29281, 29282, 29283, 29285, 29287)]
public sealed class WebWrapMessageScript : ISpellScript
{
    public static uint PullFor(float distance) => distance switch
    {
        <= 20f => 28618,
        <= 30f => 28619,
        <= 40f => 28620,
        _ => 28621,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // cmangos GetDistance(target, true, DIST_CALC_NONE): 3D, centre to centre, no bounding radii.
        uint pull = PullFor(WorldGeometry.CenterDistance3D(context.Caster, context.Target));
        GadgetCasts.Cast(context, context.Target, context.Caster, pull);
    }
}

/// <summary>
/// Choose Random Skull Pile 27896, 27930, 27938 (cmangos boss_gothik.cpp ChooseRandomSkullPile): one target only, and on
/// effect 0 the caster casts the matching Anchor to Skulls on it (27896 → 27915, 27930 → 27931, 27938 → 27937).
/// </summary>
[SpellScript(27896, 27930, 27938)]
public sealed class ChooseRandomSkullPileScript : ISpellScript
{
    public static uint AnchorFor(uint spellId) => spellId switch
    {
        27896 => 27915,
        27930 => 27931,
        27938 => 27937,
        _ => 0,
    };

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        uint anchor = AnchorFor(context.Spell.Id);
        if (context.EffectIndex != 0 || anchor == 0)
        {
            return;
        }

        GadgetCasts.Cast(context, context.Caster, context.Target, anchor);
    }
}

/// <summary>cmangos ChooseRandomSkullPile::OnInit: spell->SetMaxAffectedTargets(1).</summary>
public sealed class ChooseRandomSkullPileModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterSpellMaxTargetsOverride(27896, 1);
        system.RegisterSpellMaxTargetsOverride(27930, 1);
        system.RegisterSpellMaxTargetsOverride(27938, 1);
    }
}

/// <summary>
/// Summon Corpse Scarabs 28961 (cmangos boss_anubrekhan.cpp SummonCorpseScarabs): a dead target casts the real summon 28864;
/// a dead creature target then despawns 2 s later. A living target is left alone. cmangos passes the original caster as the
/// summon's true caster; ArcaneCore's cast API has no such parameter, so the corpse is the summoner here.
/// </summary>
[SpellScript(28961)]
public sealed class SummonCorpseScarabsScript : ISpellScript
{
    public const uint ScarabSummon = 28864;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Unit corpse = context.Target;
        if (corpse.IsAlive)
        {
            return;
        }

        GadgetCasts.Cast(context, corpse, corpse, ScarabSummon);
        WorldDummyCasts.Despawn(corpse, 2000);
    }
}

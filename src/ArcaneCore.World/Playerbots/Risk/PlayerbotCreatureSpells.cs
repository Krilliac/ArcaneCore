using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a creature's own spells add to its danger: damage per second, its hardest hit, and whether one kills outright.</summary>
internal readonly record struct CreatureSpellThreat(float Dps, int MaxHit, bool Instakill)
{
    internal static readonly CreatureSpellThreat None = new(0, 0, false);

    /// <summary>A creature whose single spell can kill the bot from <paramref name="health"/> (an instakill, or a hit that large).</summary>
    internal bool IsLethalTo(uint health) => Instakill || MaxHit > 0 && MaxHit >= health;
}

/// <summary>
/// A creature's spells as the risk estimate sees them (pure over the data, cached per spell store and entry): the CAST actions of
/// its EventAI rows (cmangos <c>creature_ai_scripts</c> action 11, <c>ACTION_T_CAST</c>; the creature's events in
/// <see cref="EventAiEngine.Holders"/>) and each spell's damage from <c>spell_template</c>: school damage, weapon damage, health
/// leech and the ticks of a periodic damage aura, the dice at their top; <c>SPELL_EFFECT_INSTAKILL</c> kills outright (the Scourge
/// invasion's Skeletal Soldier, Scourge Strike 28265, which killed the live replay's Dawnrover five times in ten minutes). A cast
/// repeats every <c>(Param3 + Param4) / 2</c> ms of its event (the in-combat timer's repeat), 10 s when the row gives none. C# AIs
/// and spells a creature casts any other way are not seen.
/// </summary>
internal static class PlayerbotCreatureSpells
{
    internal const byte ActionCast = 11;
    internal const uint DefaultRepeatMs = 10_000;

    private static readonly ConditionalWeakTable<SpellStore, Dictionary<uint, CreatureSpellThreat>> Cache = [];

    internal static CreatureSpellThreat Of(Creature creature, SpellStore? store)
    {
        if (store is null || creature.AI is not CreatureEventAI ai) return CreatureSpellThreat.None;
        Dictionary<uint, CreatureSpellThreat> cache = Cache.GetOrCreateValue(store);
        if (cache.TryGetValue(creature.Entry, out CreatureSpellThreat known)) return known;
        CreatureSpellThreat threat = Of(ai.Engine.Holders.Select(h => h.Event), store.Get);
        if (cache.Count < 4096) cache[creature.Entry] = threat;
        return threat;
    }

    internal static CreatureSpellThreat Of(IEnumerable<CreatureAiEvent> events, Func<uint, SpellInfo?> spells)
    {
        float dps = 0;
        int maxHit = 0;
        bool instakill = false;
        foreach (CreatureAiEvent row in events)
        {
            foreach (CreatureAiAction action in row.Actions)
            {
                if (action.Type != ActionCast || action.Param1 <= 0 || spells((uint)action.Param1) is not { } spell) continue;
                (int hit, bool kills) = Damage(spell);
                instakill |= kills;
                if (hit <= 0) continue;
                maxHit = Math.Max(maxHit, hit);
                int repeat = (Math.Max(0, row.Param3) + Math.Max(0, row.Param4)) / 2;
                dps += hit / ((repeat > 0 ? repeat : DefaultRepeatMs) / 1000f) * (row.Chance is > 0 and <= 100 ? row.Chance / 100f : 1f);
            }
        }

        return new CreatureSpellThreat(dps, maxHit, instakill);
    }

    /// <summary>One cast's damage (dice at their top; a periodic aura's every tick) and whether it kills outright.</summary>
    internal static (int Damage, bool Instakill) Damage(SpellInfo spell)
    {
        int damage = 0;
        bool instakill = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            int points = effect.BasePoints + Math.Max(1, effect.DieSides);
            switch (effect.Effect)
            {
                case SpellEffectName.Instakill:
                    instakill = true;
                    break;
                case SpellEffectName.SchoolDamage or SpellEffectName.HealthLeech or SpellEffectName.WeaponDamage
                    or SpellEffectName.WeaponDamageNoschool or SpellEffectName.NormalizedWeaponDmg:
                    damage += Math.Max(0, points);
                    break;
                case SpellEffectName.ApplyAura when effect.AuraType == AuraType.PeriodicDamage && effect.Amplitude > 0:
                    damage += Math.Max(0, points) * Math.Max(1, spell.GetDuration() / (int)effect.Amplitude);
                    break;
            }
        }

        return (damage, instakill);
    }
}
